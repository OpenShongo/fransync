using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Fransync.Client.Services.Storage;

public class LocalBlockStore : ILocalBlockStore
{
    private readonly string _blocksBasePath;
    private readonly string _cacheBasePath;
    private readonly string _referencesBasePath;
    private readonly ILogger<LocalBlockStore> _logger;
    private readonly SemaphoreSlim _fileOperationSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _accessTimes = new();

    public LocalBlockStore(string basePath, ILogger<LocalBlockStore> logger)
    {
        var fransyncPath = Path.Combine(basePath, ".fransync");
        _blocksBasePath = Path.Combine(fransyncPath, "blocks");
        _cacheBasePath = Path.Combine(_blocksBasePath, "cache");
        _referencesBasePath = Path.Combine(_blocksBasePath, "refs");
        _logger = logger;

        EnsureDirectoryStructure();
    }

    private void EnsureDirectoryStructure()
    {
        Directory.CreateDirectory(_blocksBasePath);
        Directory.CreateDirectory(_cacheBasePath);
        Directory.CreateDirectory(_referencesBasePath);
    }

    public async Task StoreBlockAsync(string fileId, int blockIndex, byte[] data, CancellationToken cancellationToken = default)
    {
        var blockHash = ComputeHash(data);

        // Store the actual block data by hash (deduplication)
        await StoreBlockByHashAsync(data, cancellationToken);

        // Create reference from fileId/blockIndex to hash
        var refPath = GetReferencePath(fileId, blockIndex);
        var refInfo = new BlockReference
        {
            BlockHash = blockHash,
            Size = data.Length,
            StoredAt = DateTime.UtcNow
        };

        await _fileOperationSemaphore.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(refPath)!);
            var json = JsonSerializer.Serialize(refInfo);
            await File.WriteAllTextAsync(refPath, json, cancellationToken);

            _accessTimes[blockHash] = DateTime.UtcNow;

            _logger.LogDebug("Stored block {FileId}#{BlockIndex} -> {Hash}",
                fileId, blockIndex, blockHash[..8]);
        }
        finally
        {
            _fileOperationSemaphore.Release();
        }
    }

    public async Task<byte[]?> GetBlockAsync(string fileId, int blockIndex, CancellationToken cancellationToken = default)
    {
        var refPath = GetReferencePath(fileId, blockIndex);
        if (!File.Exists(refPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(refPath, cancellationToken);
            var refInfo = JsonSerializer.Deserialize<BlockReference>(json);

            if (refInfo?.BlockHash != null)
            {
                _accessTimes[refInfo.BlockHash] = DateTime.UtcNow;
                return await GetBlockByHashAsync(refInfo.BlockHash, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read block reference {FileId}#{BlockIndex}", fileId, blockIndex);
        }

        return null;
    }

    public async Task<bool> HasBlockAsync(string fileId, int blockIndex, CancellationToken cancellationToken = default)
    {
        var refPath = GetReferencePath(fileId, blockIndex);
        if (!File.Exists(refPath)) return false;

        try
        {
            var json = await File.ReadAllTextAsync(refPath, cancellationToken);
            var refInfo = JsonSerializer.Deserialize<BlockReference>(json);

            return refInfo?.BlockHash != null &&
                   await HasBlockByHashAsync(refInfo.BlockHash, cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> StoreBlockByHashAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        var blockHash = ComputeHash(data);
        var hashPath = GetHashPath(blockHash);

        // Only store if not already present (deduplication)
        if (!File.Exists(hashPath))
        {
            await _fileOperationSemaphore.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(hashPath)!);
                await File.WriteAllBytesAsync(hashPath, data, cancellationToken);

                _logger.LogDebug("Stored new block with hash {Hash} ({Size} bytes)",
                    blockHash[..8], data.Length);
            }
            finally
            {
                _fileOperationSemaphore.Release();
            }
        }

        _accessTimes[blockHash] = DateTime.UtcNow;
        return blockHash;
    }

    public async Task<byte[]?> GetBlockByHashAsync(string blockHash, CancellationToken cancellationToken = default)
    {
        var hashPath = GetHashPath(blockHash);
        if (!File.Exists(hashPath)) return null;

        try
        {
            _accessTimes[blockHash] = DateTime.UtcNow;
            return await File.ReadAllBytesAsync(hashPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read block {Hash}", blockHash[..8]);
            return null;
        }
    }

    public async Task<bool> HasBlockByHashAsync(string blockHash, CancellationToken cancellationToken = default)
    {
        var hashPath = GetHashPath(blockHash);
        return await Task.FromResult(File.Exists(hashPath));
    }

    public async Task DeleteBlocksAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var fileRefDir = Path.Combine(_referencesBasePath, NormalizeFileId(fileId));
        if (!Directory.Exists(fileRefDir)) return;

        await _fileOperationSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Collect hashes before deleting references (for potential cleanup)
            var orphanedHashes = new List<string>();

            foreach (var refFile in Directory.GetFiles(fileRefDir, "*.ref"))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(refFile, cancellationToken);
                    var refInfo = JsonSerializer.Deserialize<BlockReference>(json);
                    if (refInfo?.BlockHash != null)
                    {
                        orphanedHashes.Add(refInfo.BlockHash);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read reference file {File}", refFile);
                }
            }

            // Delete the entire reference directory for this file
            Directory.Delete(fileRefDir, true);

            _logger.LogInformation("Deleted {Count} block references for file {FileId}",
                orphanedHashes.Count, fileId);

            // Note: We don't immediately delete the hash files since they might be referenced by other files
            // Use CleanupOrphanedBlocksAsync() for periodic cleanup
        }
        finally
        {
            _fileOperationSemaphore.Release();
        }
    }

    public async Task<byte[]?> AssembleFileAsync(string fileId, int totalBlocks, CancellationToken cancellationToken = default)
    {
        var blocks = new List<byte[]?>(totalBlocks);

        for (int i = 0; i < totalBlocks; i++)
        {
            var block = await GetBlockAsync(fileId, i, cancellationToken);
            if (block == null)
            {
                _logger.LogWarning("Missing block {BlockIndex} for file {FileId}", i, fileId);
                return null;
            }
            blocks.Add(block);
        }

        // Concatenate all blocks
        var totalSize = blocks.Sum(b => b?.Length ?? 0);
        var result = new byte[totalSize];
        var offset = 0;

        foreach (var block in blocks)
        {
            if (block != null)
            {
                Buffer.BlockCopy(block, 0, result, offset, block.Length);
                offset += block.Length;
            }
        }

        _logger.LogDebug("Assembled file {FileId} from {BlockCount} blocks ({Size} bytes)",
            fileId, totalBlocks, totalSize);

        return result;
    }

    public async Task<bool> CanAssembleFileAsync(string fileId, int totalBlocks, CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < totalBlocks; i++)
        {
            if (!await HasBlockAsync(fileId, i, cancellationToken))
            {
                return false;
            }
        }
        return true;
    }

    public async Task<IEnumerable<string>> GetMissingBlocksAsync(string fileId, int totalBlocks, CancellationToken cancellationToken = default)
    {
        var missingBlocks = new List<string>();

        for (int i = 0; i < totalBlocks; i++)
        {
            if (!await HasBlockAsync(fileId, i, cancellationToken))
            {
                missingBlocks.Add($"{fileId}#{i}");
            }
        }

        return missingBlocks;
    }

    public async Task<long> GetStorageUsageAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            if (!Directory.Exists(_cacheBasePath)) return 0L;

            return Directory.GetFiles(_cacheBasePath, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }, cancellationToken);
    }

    public async Task<int> GetBlockCountAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            if (!Directory.Exists(_cacheBasePath)) return 0;

            return Directory.GetFiles(_cacheBasePath, "*", SearchOption.AllDirectories).Length;
        }, cancellationToken);
    }

    public async Task CleanupOrphanedBlocksAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_cacheBasePath) || !Directory.Exists(_referencesBasePath))
            return;

        await _fileOperationSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Collect all referenced hashes
            var referencedHashes = new HashSet<string>();

            foreach (var refFile in Directory.GetFiles(_referencesBasePath, "*.ref", SearchOption.AllDirectories))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(refFile, cancellationToken);
                    var refInfo = JsonSerializer.Deserialize<BlockReference>(json);
                    if (refInfo?.BlockHash != null)
                    {
                        referencedHashes.Add(refInfo.BlockHash);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read reference file {File}", refFile);
                }
            }

            // Find orphaned blocks
            var orphanedCount = 0;
            foreach (var hashFile in Directory.GetFiles(_cacheBasePath, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(hashFile);
                if (!referencedHashes.Contains(fileName))
                {
                    try
                    {
                        File.Delete(hashFile);
                        orphanedCount++;

                        // Also remove from access times cache
                        _accessTimes.TryRemove(fileName, out _);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete orphaned block {File}", hashFile);
                    }
                }
            }

            if (orphanedCount > 0)
            {
                _logger.LogInformation("Cleaned up {Count} orphaned blocks", orphanedCount);
            }
        }
        finally
        {
            _fileOperationSemaphore.Release();
        }
    }

    public async Task EvictLeastRecentlyUsedAsync(long targetSizeBytes, CancellationToken cancellationToken = default)
    {
        var currentSize = await GetStorageUsageAsync(cancellationToken);
        if (currentSize <= targetSizeBytes) return;

        await _fileOperationSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Get all blocks sorted by last access time (oldest first)
            var blockFiles = Directory.GetFiles(_cacheBasePath, "*", SearchOption.AllDirectories)
                .Select(file => new
                {
                    Path = file,
                    Hash = Path.GetFileName(file),
                    Size = new FileInfo(file).Length,
                    LastAccess = _accessTimes.GetValueOrDefault(Path.GetFileName(file), File.GetLastAccessTimeUtc(file))
                })
                .OrderBy(b => b.LastAccess)
                .ToList();

            var evictedSize = 0L;
            var evictedCount = 0;

            foreach (var block in blockFiles)
            {
                if (currentSize - evictedSize <= targetSizeBytes) break;

                try
                {
                    File.Delete(block.Path);
                    _accessTimes.TryRemove(block.Hash, out _);
                    evictedSize += block.Size;
                    evictedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to evict block {Hash}", block.Hash[..8]);
                }
            }

            _logger.LogInformation("Evicted {Count} blocks ({Size:N0} bytes) to meet target size",
                evictedCount, evictedSize);
        }
        finally
        {
            _fileOperationSemaphore.Release();
        }
    }

    public async Task<IEnumerable<string>> GetCachedBlockHashesAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            if (!Directory.Exists(_cacheBasePath)) return Enumerable.Empty<string>();

            return Directory.GetFiles(_cacheBasePath, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList()!;
        }, cancellationToken);
    }

    private string GetReferencePath(string fileId, int blockIndex)
    {
        var normalizedFileId = NormalizeFileId(fileId);
        return Path.Combine(_referencesBasePath, normalizedFileId, $"{blockIndex}.ref");
    }

    private string GetHashPath(string blockHash)
    {
        // Create subdirectories based on first 2 characters of hash for better filesystem performance
        var subDir = blockHash.Length >= 2 ? blockHash[..2] : "00";
        return Path.Combine(_cacheBasePath, subDir, blockHash);
    }

    private static string NormalizeFileId(string fileId)
    {
        return fileId.Replace('\\', '/').Replace(':', '_').Replace('*', '_').Replace('?', '_');
    }

    private static string ComputeHash(byte[] data)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(data)).ToLowerInvariant();
    }

    private class BlockReference
    {
        public string BlockHash { get; set; } = string.Empty;
        public int Size { get; set; }
        public DateTime StoredAt { get; set; }
    }
}