using Fransync.Client.Configuration;
using Fransync.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Fransync.Client.Services;

public class ManifestGenerationService : IManifestGenerationService
{
    private readonly SyncClientOptions _options;
    private readonly IFileBlockService _fileBlockService;
    private readonly IBridgeHttpClient _bridgeHttpClient;
    private readonly ILogger<ManifestGenerationService> _logger;
    private readonly ConcurrentDictionary<string, ManifestGenerationTask> _activeTasks = new();

    public ManifestGenerationService(
        IOptions<SyncClientOptions> options,
        IFileBlockService fileBlockService,
        IBridgeHttpClient bridgeHttpClient,
        ILogger<ManifestGenerationService> logger)
    {
        _options = options.Value;
        _fileBlockService = fileBlockService;
        _bridgeHttpClient = bridgeHttpClient;
        _logger = logger;
    }

    public async Task<ManifestGenerationResult> GenerateManifestAsync(string relativePath, string sourcePath, CancellationToken cancellationToken = default)
    {
        var result = new ManifestGenerationResult { RelativePath = relativePath };

        try
        {
            if (!File.Exists(sourcePath))
            {
                result.ErrorMessage = $"Source file not found: {sourcePath}";
                return result;
            }

            var fileInfo = new FileInfo(sourcePath);
            _logger.LogDebug("Generating manifest for {RelativePath} (Size: {Size} bytes)", relativePath, fileInfo.Length);

            var manifest = new FileManifest
            {
                FileName = Path.GetFileName(sourcePath),
                RelativePath = relativePath,
                FileSize = fileInfo.Length,
                BlockSize = _options.BlockSize
            };

            // Generate blocks
            var blocks = _fileBlockService.SplitFileIntoBlocks(sourcePath, _options.BlockSize).ToList();
            foreach (var (index, block, hash) in blocks)
            {
                manifest.Blocks.Add(new BlockInfo { Index = index, Hash = hash, Size = block.Length });
            }

            result.Manifest = manifest;
            result.Success = true;

            _logger.LogDebug("Generated manifest for {RelativePath}: {BlockCount} blocks", relativePath, manifest.Blocks.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating manifest for {RelativePath}", relativePath);
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public async Task<List<ManifestGenerationResult>> GenerateManifestsForSnapshotAsync(DirectorySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var results = new List<ManifestGenerationResult>();
        var semaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount); // Limit concurrent operations

        _logger.LogInformation("Starting manifest generation for {FileCount} files", snapshot.Files.Count);

        var tasks = snapshot.Files.Values.Select(async fileSnapshot =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                // Construct the source path - this assumes the snapshot is from the monitored directory
                var sourcePath = Path.Combine(_options.MonitoredPath, fileSnapshot.RelativePath);
                return await GenerateManifestAsync(fileSnapshot.RelativePath, sourcePath, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var manifestResults = await Task.WhenAll(tasks);
        results.AddRange(manifestResults);

        var successful = results.Count(r => r.Success);
        var failed = results.Count(r => !r.Success);

        _logger.LogInformation("Manifest generation completed: {Successful} successful, {Failed} failed", successful, failed);

        return results;
    }

    public async Task<bool> UploadManifestAndBlocksAsync(FileManifest manifest, string sourcePath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Uploading manifest and blocks for {RelativePath}", manifest.RelativePath);

            // Upload manifest first
            var manifestUploaded = await _bridgeHttpClient.SendManifestAsync(manifest, cancellationToken);
            if (!manifestUploaded)
            {
                _logger.LogError("Failed to upload manifest for {RelativePath}", manifest.RelativePath);
                return false;
            }

            // Upload blocks
            var blocks = _fileBlockService.SplitFileIntoBlocks(sourcePath, _options.BlockSize).ToList();
            int successfulBlocks = 0;

            foreach (var (index, block, hash) in blocks)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var blockUploaded = await _bridgeHttpClient.SendBlockAsync(manifest.RelativePath, index, hash, block, cancellationToken);
                if (blockUploaded)
                {
                    successfulBlocks++;
                }
                else
                {
                    _logger.LogWarning("Failed to upload block {Index} for {RelativePath}", index, manifest.RelativePath);
                }
            }

            var allBlocksUploaded = successfulBlocks == blocks.Count;
            if (allBlocksUploaded)
            {
                _logger.LogInformation("Successfully uploaded manifest and all blocks for {RelativePath}", manifest.RelativePath);
            }
            else
            {
                _logger.LogWarning("Partially uploaded file {RelativePath}: {Successful}/{Total} blocks",
                    manifest.RelativePath, successfulBlocks, blocks.Count);
            }

            return allBlocksUploaded;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading manifest and blocks for {RelativePath}", manifest.RelativePath);
            return false;
        }
    }
}