using Fransync.Client.Configuration;
using Fransync.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fransync.Client.Services;

public class DirectorySnapshotService(
    IOptions<SyncClientOptions> options,
    HttpClient httpClient,
    ILogger<DirectorySnapshotService> logger)
    : IDirectorySnapshotService
{
    private readonly SyncClientOptions _options = options.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<DirectorySnapshot> CreateSnapshotAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Creating directory snapshot for: {DirectoryPath}", directoryPath);

        var snapshot = new DirectorySnapshot();
        var files = new List<FileSnapshot>();

        if (!Directory.Exists(directoryPath))
        {
            logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return snapshot;
        }

        try
        {
            var allFiles = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
                .Where(f => !ShouldIgnoreFile(f))
                .ToList();

            logger.LogDebug("Found {FileCount} files to process", allFiles.Count);

            foreach (var filePath in allFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    var relativePath = Path.GetRelativePath(directoryPath, filePath).Replace('\\', '/');

                    var contentHash = await CalculateFileHashAsync(filePath, cancellationToken);
                    var blockCount = CalculateBlockCount(fileInfo.Length, _options.BlockSize);

                    var fileSnapshot = new FileSnapshot
                    {
                        RelativePath = relativePath,
                        FileName = fileInfo.Name,
                        FileSize = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTimeUtc,
                        ContentHash = contentHash,
                        BlockCount = blockCount
                    };

                    files.Add(fileSnapshot);
                    snapshot.Files[relativePath] = fileSnapshot;
                    snapshot.TotalSize += fileInfo.Length;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error processing file: {FilePath}", filePath);
                }
            }

            snapshot.TotalFiles = files.Count;
            snapshot.DirectoryHash = CalculateDirectoryHash(files);

            logger.LogInformation("Created snapshot: {FileCount} files, {TotalSize} bytes, Hash: {Hash}",
                snapshot.TotalFiles, snapshot.TotalSize, snapshot.DirectoryHash);

            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating directory snapshot");
            return snapshot;
        }
    }

    public async Task<DirectorySnapshot?> GetRemoteSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogDebug("Requesting remote directory snapshot");

            var response = await httpClient.GetAsync($"{_options.BridgeEndpoint}/snapshot", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to get remote snapshot: {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var snapshot = JsonSerializer.Deserialize<DirectorySnapshot>(json, JsonOptions);

            if (snapshot != null)
            {
                logger.LogInformation("Received remote snapshot: {FileCount} files, Hash: {Hash}",
                    snapshot.TotalFiles, snapshot.DirectoryHash);
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting remote snapshot");
            return null;
        }
    }

    public DirectoryComparisonResult CompareSnapshots(DirectorySnapshot? remote, DirectorySnapshot local)
    {
        var result = new DirectoryComparisonResult();

        if (remote == null)
        {
            logger.LogInformation("No remote snapshot available, all local files will be uploaded");
            result.FilesToDownload.AddRange(local.Files.Keys);
            return result;
        }

        logger.LogInformation("Comparing snapshots - Remote: {RemoteFiles} files, Local: {LocalFiles} files",
            remote.TotalFiles, local.TotalFiles);

        // Check if directory hashes match (quick comparison)
        if (remote.DirectoryHash == local.DirectoryHash)
        {
            logger.LogInformation("Directory hashes match, no sync required");
            return result;
        }

        // Files that exist remotely but not locally (need to download)
        foreach (var remoteFile in remote.Files.Values)
        {
            if (!local.Files.ContainsKey(remoteFile.RelativePath))
            {
                result.FilesToDownload.Add(remoteFile.RelativePath);
                logger.LogDebug("File to download: {RelativePath}", remoteFile.RelativePath);
            }
        }

        // Files that exist locally but not remotely (should be uploaded - handled by file watcher)
        // Files that exist in both but differ (need to update)
        foreach (var localFile in local.Files.Values)
        {
            if (remote.Files.TryGetValue(localFile.RelativePath, out var remoteFile))
            {
                // File exists in both, check if it needs updating
                if (NeedsUpdate(remoteFile, localFile))
                {
                    result.FilesToUpdate.Add(localFile.RelativePath);
                    logger.LogDebug("File to update: {RelativePath}", localFile.RelativePath);
                }
            }
        }

        // Detect potential renames (files with same content hash but different paths)
        DetectRenames(remote, local, result);

        logger.LogInformation("Comparison complete - Download: {Download}, Update: {Update}, Delete: {Delete}, Rename: {Rename}",
            result.FilesToDownload.Count, result.FilesToUpdate.Count, result.FilesToDelete.Count, result.FilesToRename.Count);

        return result;
    }

    public async Task<string> CalculateFileHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hashBytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error calculating hash for file: {FilePath}", filePath);
            return string.Empty;
        }
    }

    public async Task<DirectorySnapshot?> GetSourceSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogDebug("Requesting source directory snapshot");

            var response = await httpClient.GetAsync($"{_options.BridgeEndpoint}/source-snapshot", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to get source snapshot: {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var snapshot = JsonSerializer.Deserialize<DirectorySnapshot>(json, JsonOptions);

            if (snapshot != null)
            {
                logger.LogInformation("Received source snapshot: {FileCount} files, Hash: {Hash}",
                    snapshot.TotalFiles, snapshot.DirectoryHash);
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting source snapshot");
            return null;
        }
    }

    public async Task<DirectorySnapshot?> GetRemoteSnapshotForManifestGenerationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogDebug("Requesting remote snapshot for manifest generation");

            // First try to get the source snapshot
            var response = await httpClient.GetAsync($"{_options.BridgeEndpoint}/source-snapshot", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var snapshot = JsonSerializer.Deserialize<DirectorySnapshot>(json, JsonOptions);

                if (snapshot != null)
                {
                    logger.LogInformation("Found existing source snapshot: {FileCount} files", snapshot.TotalFiles);
                    return snapshot;
                }
            }

            // If no source snapshot exists, use the current bridge snapshot
            logger.LogInformation("No source snapshot found, using bridge snapshot");
            return await GetRemoteSnapshotAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting remote snapshot for manifest generation");
            return null;
        }
    }

    private bool ShouldIgnoreFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.StartsWith('.') ||
               fileName.StartsWith('~') ||
               fileName.EndsWith(".tmp") ||
               fileName.EndsWith(".swp") ||
               fileName.Contains("~$") ||
               fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    private static int CalculateBlockCount(long fileSize, int blockSize)
    {
        return (int)Math.Ceiling((double)fileSize / blockSize);
    }

    private string CalculateDirectoryHash(List<FileSnapshot> files)
    {
        // Create a deterministic hash of the entire directory structure
        var sortedFiles = files.OrderBy(f => f.RelativePath).ToList();
        var sb = new StringBuilder();

        foreach (var file in sortedFiles)
        {
            sb.Append($"{file.RelativePath}|{file.FileSize}|{file.ContentHash}|");
        }

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes);
    }

    private static bool NeedsUpdate(FileSnapshot remote, FileSnapshot local)
    {
        // File needs update if:
        // 1. Content hash is different
        // 2. File size is different
        // 3. Remote file is newer
        return remote.ContentHash != local.ContentHash ||
               remote.FileSize != local.FileSize ||
               remote.LastModified > local.LastModified;
    }

    private void DetectRenames(DirectorySnapshot remote, DirectorySnapshot local, DirectoryComparisonResult result)
    {
        // Find files that have the same content hash but different paths
        var remoteByHash = remote.Files.Values
            .Where(f => !string.IsNullOrEmpty(f.ContentHash))
            .GroupBy(f => f.ContentHash)
            .ToDictionary(g => g.Key, g => g.ToList());

        var localByHash = local.Files.Values
            .Where(f => !string.IsNullOrEmpty(f.ContentHash))
            .GroupBy(f => f.ContentHash)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (hash, remoteFiles) in remoteByHash)
        {
            if (localByHash.TryGetValue(hash, out var localFiles))
            {
                // Same content exists, check if paths are different
                foreach (var remoteFile in remoteFiles)
                {
                    var matchingLocal = localFiles.FirstOrDefault(l => l.RelativePath != remoteFile.RelativePath);
                    if (matchingLocal != null)
                    {
                        result.FilesToRename.Add((matchingLocal.RelativePath, remoteFile.RelativePath));
                        logger.LogDebug("Potential rename detected: {OldPath} -> {NewPath}",
                            matchingLocal.RelativePath, remoteFile.RelativePath);
                    }
                }
            }
        }
    }
}