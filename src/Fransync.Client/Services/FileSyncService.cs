using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Fransync.Client.Configuration;
using Fransync.Client.Models;
using Fransync.Client.Models.Queue;
using Fransync.Client.Services.Queue;
using Fransync.Client.Services.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fransync.Client.Services;

public class FileSyncService : IFileSyncService
{
    private readonly ILocalQueueManager _queueManager;
    private readonly ILocalBlockStore _blockStore;
    private readonly IFileBlockService _fileBlockService;
    private readonly IBridgeHttpClient _bridgeHttpClient;
    private readonly HttpClient _httpClient;
    private readonly IDirectorySnapshotService _snapshotService;
    private readonly SyncClientOptions _options;
    private readonly ILogger<FileSyncService> _logger;

    private readonly ConcurrentDictionary<string, DateTime> _lastProcessedTimes = new();
    private readonly ConcurrentDictionary<string, Task> _activeOperations = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    private readonly Timer _queueProcessorTimer;

    // Events for monitoring
    public event Func<SyncProgressEvent, Task>? SyncProgressChanged;
    public event Func<SyncErrorEvent, Task>? SyncErrorOccurred;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public FileSyncService(
        ILocalQueueManager queueManager,
        ILocalBlockStore blockStore,
        IFileBlockService fileBlockService,
        IBridgeHttpClient bridgeHttpClient,
        HttpClient httpClient,
        IDirectorySnapshotService snapshotService,
        IOptions<SyncClientOptions> options,
        ILogger<FileSyncService> logger)
    {
        _queueManager = queueManager;
        _blockStore = blockStore;
        _fileBlockService = fileBlockService;
        _bridgeHttpClient = bridgeHttpClient;
        _httpClient = httpClient;
        _snapshotService = snapshotService;
        _options = options.Value;
        _logger = logger;

        // Start background queue processor
        _queueProcessorTimer = new Timer(ProcessQueues, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    public async Task ProcessFileOperationAsync(FileOperationEvent operation, CancellationToken cancellationToken = default)
    {
        var operationKey = $"{operation.OperationType}:{operation.RelativePath}";

        // Prevent concurrent operations on the same file
        if (_activeOperations.ContainsKey(operationKey))
        {
            _logger.LogDebug("Operation already in progress for {Path}, skipping", operation.RelativePath);
            return;
        }

        // Debouncing: Skip if we recently processed this file
        if (ShouldSkipDueToDebouncing(operation))
        {
            _logger.LogDebug("Skipping duplicate {OperationType} operation for {Path}",
                operation.OperationType, operation.RelativePath);
            return;
        }

        var operationTask = ProcessFileOperationInternalAsync(operation, cancellationToken);
        _activeOperations[operationKey] = operationTask;

        try
        {
            await operationTask;
        }
        finally
        {
            _activeOperations.TryRemove(operationKey, out _);
            _lastProcessedTimes[operation.RelativePath] = DateTime.UtcNow;
        }
    }

    public async Task RegisterDirectoryStructureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Registering complete directory structure with bridge server");

            // Create snapshot of the monitored path (source)
            var snapshot = await _snapshotService.CreateSnapshotAsync(_options.MonitoredPath, cancellationToken);

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_options.BridgeEndpoint}/register-directory", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully registered directory structure: {FileCount} files", snapshot.TotalFiles);
            }
            else
            {
                _logger.LogWarning("Failed to register directory structure: {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering directory structure");
        }
    }

    public async Task ProcessFileCreatedAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var relativePath = GetRelativePath(filePath);
        await QueueFileUpload(relativePath, cancellationToken);
        await NotifyProgress("File Created", relativePath, 0, 1, "Queued");
    }

    public async Task ProcessFileModifiedAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var relativePath = GetRelativePath(filePath);
        await QueueFileUpload(relativePath, cancellationToken);
        await NotifyProgress("File Modified", relativePath, 0, 1, "Queued");
    }

    public async Task ProcessFileDeletedAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var relativePath = GetRelativePath(filePath);
        await QueueFileDelete(relativePath, cancellationToken);
        await NotifyProgress("File Deleted", relativePath, 1, 1, "Queued");
    }

    public async Task ProcessFileRenamedAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        var oldRelativePath = GetRelativePath(oldPath);
        var newRelativePath = GetRelativePath(newPath);
        await QueueFileRename(oldRelativePath, newRelativePath, cancellationToken);
        await NotifyProgress("File Renamed", newRelativePath, 1, 1, "Queued");
    }

    public async Task ProcessFileOperationQueueAsync(CancellationToken cancellationToken = default)
    {
        await ProcessFileOperationQueue(cancellationToken);
    }

    public async Task InitiateFileDownloadAsync(string relativePath, string manifestHash, CancellationToken cancellationToken = default)
    {
        try
        {
            var localPath = Path.Combine(_options.SyncWithPath, relativePath);
            if (File.Exists(localPath))
            {
                _logger.LogDebug("File {RelativePath} already exists locally, skipping download", relativePath);
                return;
            }

            var downloadItem = new DownloadQueueItem
            {
                RelativePath = relativePath,
                DestinationPath = localPath,
                ManifestHash = manifestHash
            };

            await _queueManager.EnqueueDownloadAsync(downloadItem, cancellationToken);
            await NotifyProgress("Download Initiated", relativePath, 0, 1, "Queued");

            _logger.LogInformation("Initiated download for {RelativePath}", relativePath);
        }
        catch (Exception ex)
        {
            await NotifyError("Initiate Download", relativePath, ex.Message, ex, true);
        }
    }

    public async Task HandleIncomingManifestAsync(ManifestNotification notification, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Received manifest notification for {RelativePath}, waiting for upload completion",
                notification.RelativePath);
            await NotifyProgress("Manifest Received", notification.RelativePath, 0, notification.BlockCount, "Waiting");
        }
        catch (Exception ex)
        {
            await NotifyError("Handle Manifest", notification.RelativePath, ex.Message, ex, false);
        }
    }

    public async Task HandleFileUploadCompleteAsync(FileUploadCompleteNotification notification, CancellationToken cancellationToken = default)
    {
        try
        {
            var manifestHash = $"manifest_{notification.RelativePath}_{notification.CompletedAt.Ticks}";
            await InitiateFileDownloadAsync(notification.RelativePath, manifestHash, cancellationToken);

            _logger.LogInformation("File upload completed, initiated download for {RelativePath}",
                notification.RelativePath);
        }
        catch (Exception ex)
        {
            await NotifyError("Handle Upload Complete", notification.RelativePath, ex.Message, ex, true);
        }
    }

    public async Task<SyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default)
    {
        var pendingUploads = await _queueManager.GetQueueCountAsync(QueueItemType.Upload, cancellationToken);
        var pendingDownloads = await _queueManager.GetQueueCountAsync(QueueItemType.Download, cancellationToken);
        var pendingOperations = await _queueManager.GetQueueCountAsync(QueueItemType.FileOperation, cancellationToken);
        var failedItems = await _queueManager.GetFailedItemsAsync(cancellationToken);

        return new SyncStatus
        {
            PendingUploads = pendingUploads,
            PendingDownloads = pendingDownloads,
            PendingOperations = pendingOperations,
            LastActivity = _lastProcessedTimes.Values.DefaultIfEmpty(DateTime.MinValue).Max(),
            IsHealthy = failedItems.Count() < 10,
            Issues = failedItems.Select(f => $"{f.Type}: {f.RelativePath} - {f.ErrorMessage}").ToList()
        };
    }

    public async Task<IEnumerable<QueueItemBase>> GetQueueStatusAsync(CancellationToken cancellationToken = default)
    {
        var uploads = await _queueManager.GetPendingUploadsAsync(cancellationToken);
        var downloads = await _queueManager.GetPendingDownloadsAsync(cancellationToken);
        var operations = await _queueManager.GetPendingFileOperationsAsync(cancellationToken);

        return uploads.Cast<QueueItemBase>()
            .Concat(downloads.Cast<QueueItemBase>())
            .Concat(operations.Cast<QueueItemBase>())
            .OrderBy(q => q.CreatedAt);
    }

    public async Task<StorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken = default)
    {
        var blockCacheSize = await _blockStore.GetStorageUsageAsync(cancellationToken);
        var blockCount = await _blockStore.GetBlockCountAsync(cancellationToken);

        // Calculate queue size (sum of all pending items)
        var uploadCount = await _queueManager.GetQueueCountAsync(QueueItemType.Upload, cancellationToken);
        var downloadCount = await _queueManager.GetQueueCountAsync(QueueItemType.Download, cancellationToken);
        var operationCount = await _queueManager.GetQueueCountAsync(QueueItemType.FileOperation, cancellationToken);
        var queueSize = uploadCount + downloadCount + operationCount;

        return new StorageInfo
        {
            BlockCacheSize = blockCacheSize,
            BlockCount = blockCount,
            QueueSize = queueSize, // SET the missing property
            DiskUsagePercent = CalculateDiskUsagePercent(),
            CacheRetention = TimeSpan.FromDays(7)
        };
    }

    public async Task ForceResyncAsync(string? specificPath = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Force resync requested for {Path}", specificPath ?? "all files");

        if (specificPath != null)
        {
            // Force resync specific file
            await QueueFileUpload(specificPath, cancellationToken);
        }
        else
        {
            // Clear debouncing cache to allow re-processing of all files
            _lastProcessedTimes.Clear();
            _logger.LogInformation("Cleared debouncing cache for force resync");
        }
    }

    public async Task ProcessUploadQueueAsync(CancellationToken cancellationToken = default)
    {
        await ProcessUploadQueue(cancellationToken);
    }

    public async Task ProcessDownloadQueueAsync(CancellationToken cancellationToken = default)
    {
        var pendingDownloads = await _queueManager.GetPendingDownloadsAsync(cancellationToken);

        foreach (var downloadItem in pendingDownloads.Take(3))
        {
            if (cancellationToken.IsCancellationRequested) break;
            await ProcessDownloadItem(downloadItem, cancellationToken);
        }
    }

    public async Task PerformMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        await PerformMaintenance(cancellationToken);
    }

    private async Task ProcessDownloadItem(DownloadQueueItem downloadItem, CancellationToken cancellationToken)
    {
        try
        {
            await _queueManager.IncrementAttemptAsync(downloadItem.Id, cancellationToken);
            await _queueManager.UpdateStatusAsync(downloadItem.Id, QueueItemStatus.Processing, null, cancellationToken);

            // Check if we can already assemble the file from local cache
            if (await _blockStore.CanAssembleFileAsync(downloadItem.RelativePath, downloadItem.ExpectedBlockCount, cancellationToken))
            {
                await CompleteDownload(downloadItem, cancellationToken);
                return;
            }

            // Get manifest from bridge
            var manifest = await _bridgeHttpClient.GetManifestAsync(downloadItem.RelativePath, cancellationToken);
            if (manifest == null)
            {
                await _queueManager.MarkFailedAsync(downloadItem.Id, "Manifest not found", cancellationToken);
                return;
            }

            // Download missing blocks
            var missingBlocks = await _blockStore.GetMissingBlocksAsync(downloadItem.RelativePath, manifest.Blocks.Count, cancellationToken);

            foreach (var missingBlockId in missingBlocks)
            {
                var parts = missingBlockId.Split('#');
                if (parts.Length == 2 && int.TryParse(parts[1], out int blockIndex))
                {
                    var blockData = await _bridgeHttpClient.GetBlockAsync(downloadItem.RelativePath, blockIndex, cancellationToken);
                    if (blockData != null)
                    {
                        await _blockStore.StoreBlockAsync(downloadItem.RelativePath, blockIndex, blockData, cancellationToken);
                        await _queueManager.UpdateDownloadProgressAsync(downloadItem.Id, blockIndex, cancellationToken);
                        await NotifyProgress("Downloading", downloadItem.RelativePath,
                            downloadItem.DownloadedBlocks.Count + 1, downloadItem.ExpectedBlockCount, "In Progress");
                    }
                }
            }

            await CompleteDownload(downloadItem, cancellationToken);
        }
        catch (Exception ex)
        {
            await _queueManager.MarkFailedAsync(downloadItem.Id, ex.Message, cancellationToken);
            await NotifyError("Download", downloadItem.RelativePath, ex.Message, ex, true);
        }
    }

    private async Task CompleteDownload(DownloadQueueItem downloadItem, CancellationToken cancellationToken)
    {
        // Assemble file from blocks
        var fileData = await _blockStore.AssembleFileAsync(downloadItem.RelativePath, downloadItem.ExpectedBlockCount, cancellationToken);
        if (fileData == null)
        {
            await _queueManager.MarkFailedAsync(downloadItem.Id, "Failed to assemble file from blocks", cancellationToken);
            return;
        }

        // Write to destination
        var destinationPath = Path.Combine(_options.SyncWithPath, downloadItem.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(destinationPath, fileData, cancellationToken);

        await _queueManager.MarkCompletedAsync(downloadItem.Id, cancellationToken);
        await NotifyProgress("Download Complete", downloadItem.RelativePath,
            downloadItem.ExpectedBlockCount, downloadItem.ExpectedBlockCount, "Completed");

        _logger.LogInformation("Successfully downloaded and assembled {FilePath} ({Size} bytes)",
            downloadItem.RelativePath, fileData.Length);
    }

    private async Task NotifyProgress(string operation, string filePath, int progress, int total, string status)
    {
        if (SyncProgressChanged != null)
        {
            await SyncProgressChanged(new SyncProgressEvent
            {
                Operation = operation,
                FilePath = filePath,
                Progress = progress,
                Total = total,
                Status = status
            });
        }
    }

    private async Task NotifyError(string operation, string filePath, string message, Exception? ex, bool isRetryable)
    {
        if (SyncErrorOccurred != null)
        {
            await SyncErrorOccurred(new SyncErrorEvent
            {
                Operation = operation,
                FilePath = filePath,
                ErrorMessage = message,
                Exception = ex,
                IsRetryable = isRetryable
            });
        }
    }

    private string GetRelativePath(string fullPath)
    {
        var monitoredPath = Path.GetFullPath(_options.MonitoredPath);
        var filePath = Path.GetFullPath(fullPath);

        if (filePath.StartsWith(monitoredPath))
        {
            return Path.GetRelativePath(monitoredPath, filePath).Replace('\\', '/');
        }

        return Path.GetFileName(fullPath);
    }

    private static double CalculateDiskUsagePercent()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:");
            return ((double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize) * 100;
        }
        catch
        {
            return 0.0;
        }
    }

    private async Task ProcessFileOperationInternalAsync(FileOperationEvent operation, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Queuing {OperationType} operation for {Path}",
                operation.OperationType, operation.RelativePath);

            switch (operation.OperationType)
            {
                case FileOperationType.Created:
                case FileOperationType.Modified:
                    await QueueFileUpload(operation.RelativePath, cancellationToken);
                    break;

                case FileOperationType.Deleted:
                    await QueueFileDelete(operation.RelativePath, cancellationToken);
                    break;

                case FileOperationType.Renamed:
                    await QueueFileRename(operation.OldRelativePath!, operation.RelativePath, cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("File operation cancelled: {OperationType} for {Path}",
                operation.OperationType, operation.RelativePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file operation: {OperationType} for {Path}",
                operation.OperationType, operation.RelativePath);
        }
    }

    private async Task QueueFileUpload(string relativePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(_options.MonitoredPath, relativePath);

        if (!File.Exists(fullPath))
        {
            _logger.LogDebug("File no longer exists: {Path}", relativePath);
            return;
        }

        // Wait for file to be fully written and available
        if (!await WaitForFileToBeReady(fullPath, cancellationToken))
        {
            _logger.LogWarning("File was not ready for reading after waiting: {Path}", relativePath);
            return;
        }

        try
        {
            var fileInfo = new FileInfo(fullPath);
            _logger.LogDebug("Queuing file upload: {Path} (Size: {Size} bytes)", relativePath, fileInfo.Length);

            // Create manifest
            var manifest = new FileManifest
            {
                FileName = Path.GetFileName(fullPath),
                RelativePath = relativePath,
                FileSize = fileInfo.Length,
                BlockSize = _options.BlockSize
            };

            // Pre-calculate blocks for manifest
            var blocks = await RetryFileOperation(
                () => _fileBlockService.SplitFileIntoBlocks(fullPath, _options.BlockSize).ToList(),
                $"splitting file {relativePath}",
                cancellationToken);

            if (blocks == null)
            {
                _logger.LogError("Failed to split file into blocks: {Path}", relativePath);
                return;
            }

            foreach (var (index, block, hash) in blocks)
            {
                manifest.Blocks.Add(new BlockInfo { Index = index, Hash = hash, Size = block.Length });
            }

            // Create upload queue item
            var uploadItem = new UploadQueueItem
            {
                FilePath = fullPath,
                RelativePath = relativePath,
                Manifest = manifest,
                FileSize = manifest.FileSize,
                BlockCount = manifest.Blocks.Count,
                FileHash = CalculateManifestHash(manifest)
            };

            await _queueManager.EnqueueUploadAsync(uploadItem, cancellationToken);

            _logger.LogInformation("Queued upload for {FilePath} ({Size} bytes, {Blocks} blocks)",
                relativePath, manifest.FileSize, manifest.Blocks.Count);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "IO error while queuing file {Path}, operation may be retried", relativePath);
            _lastProcessedTimes.TryRemove(relativePath, out _);
            throw;
        }
    }

    private async Task QueueFileDelete(string relativePath, CancellationToken cancellationToken)
    {
        var operationItem = new FileOperationQueueItem
        {
            RelativePath = relativePath,
            OperationType = FileOperationType.Deleted,
            OperationTimestamp = DateTime.UtcNow
        };

        await _queueManager.EnqueueFileOperationAsync(operationItem, cancellationToken);

        // Clean up local blocks immediately
        await _blockStore.DeleteBlocksAsync(relativePath, cancellationToken);

        _logger.LogInformation("Queued deletion for {FilePath}", relativePath);
    }

    private async Task QueueFileRename(string oldRelativePath, string newRelativePath, CancellationToken cancellationToken)
    {
        var operationItem = new FileOperationQueueItem
        {
            RelativePath = newRelativePath,
            OldRelativePath = oldRelativePath,
            OperationType = FileOperationType.Renamed,
            OperationTimestamp = DateTime.UtcNow
        };

        await _queueManager.EnqueueFileOperationAsync(operationItem, cancellationToken);

        _logger.LogInformation("Queued rename from {OldPath} to {NewPath}", oldRelativePath, newRelativePath);

        // Also queue the file upload with new name
        await QueueFileUpload(newRelativePath, cancellationToken);
    }

    private async void ProcessQueues(object? state)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

            // Process each queue type
            await ProcessUploadQueue(cts.Token);
            await ProcessDownloadQueueAsync(cts.Token);
            await ProcessFileOperationQueue(cts.Token);

            // Periodic maintenance
            if (DateTime.UtcNow.Minute % 10 == 0) // Every 10 minutes
            {
                await PerformMaintenance(cts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing queues");
        }
    }

    private async Task ProcessUploadQueue(CancellationToken cancellationToken)
    {
        var pendingUploads = await _queueManager.GetPendingUploadsAsync(cancellationToken);

        foreach (var uploadItem in pendingUploads.Take(3)) // Process max 3 at a time
        {
            if (cancellationToken.IsCancellationRequested) break;

            await ProcessUploadItem(uploadItem, cancellationToken);
        }
    }

    private async Task ProcessUploadItem(UploadQueueItem uploadItem, CancellationToken cancellationToken)
    {
        var fileLock = _fileLocks.GetOrAdd(uploadItem.RelativePath, _ => new SemaphoreSlim(1, 1));
        if (!await fileLock.WaitAsync(100, cancellationToken)) return;

        try
        {
            await _queueManager.IncrementAttemptAsync(uploadItem.Id, cancellationToken);
            await _queueManager.UpdateStatusAsync(uploadItem.Id, QueueItemStatus.Processing, null, cancellationToken);

            if (uploadItem.Manifest == null)
            {
                await _queueManager.MarkFailedAsync(uploadItem.Id, "Missing manifest", cancellationToken);
                return;
            }

            // Step 1: Upload manifest to bridge
            var manifestSent = await _bridgeHttpClient.SendManifestAsync(uploadItem.Manifest, cancellationToken);
            if (!manifestSent)
            {
                await _queueManager.MarkFailedAsync(uploadItem.Id, "Failed to send manifest", cancellationToken);
                return;
            }

            _logger.LogDebug("Uploaded manifest for {FilePath}", uploadItem.RelativePath);

            // Step 2: Process blocks - UPDATED to use SplitIntoBlocks with byte array
            if (!File.Exists(uploadItem.FilePath))
            {
                await _queueManager.MarkFailedAsync(uploadItem.Id, "Source file no longer exists", cancellationToken);
                return;
            }

            // Read file data into memory and use SplitIntoBlocks
            var fileData = await File.ReadAllBytesAsync(uploadItem.FilePath, cancellationToken);
            var blocks = _fileBlockService.SplitIntoBlocks(fileData, _options.BlockSize);

            int blocksSent = 0;
            foreach (var (index, block) in blocks.Select((b, i) => (i, b)))
            {
                if (cancellationToken.IsCancellationRequested) break;

                // Store block locally first (with deduplication)
                await _blockStore.StoreBlockAsync(uploadItem.RelativePath, index, block, cancellationToken);

                // Compute hash for the block (since SplitIntoBlocks doesn't return it)
                var blockHash = ComputeBlockHash(block);

                // Upload block to bridge
                var blockSent = await _bridgeHttpClient.SendBlockAsync(uploadItem.RelativePath, index, blockHash, block, cancellationToken);

                if (blockSent)
                {
                    blocksSent++;
                    await _queueManager.UpdateUploadProgressAsync(uploadItem.Id, index, cancellationToken);

                    // Log progress for large files
                    var totalBlocks = uploadItem.Manifest.Blocks.Count;
                    if (totalBlocks > 5 && (blocksSent % Math.Max(1, totalBlocks / 5) == 0))
                    {
                        _logger.LogDebug("Upload progress for {Path}: {Sent}/{Total} blocks",
                            uploadItem.RelativePath, blocksSent, totalBlocks);
                    }
                }
                else
                {
                    _logger.LogWarning("Failed to send block {Index} for {Path}", index, uploadItem.RelativePath);
                }
            }

            var totalBlockCount = uploadItem.Manifest.Blocks.Count;
            if (blocksSent == totalBlockCount)
            {
                await _queueManager.MarkCompletedAsync(uploadItem.Id, cancellationToken);
                _logger.LogInformation("Successfully uploaded file: {Path} ({Size} bytes, {Blocks} blocks)",
                    uploadItem.RelativePath, uploadItem.FileSize, totalBlockCount);
            }
            else
            {
                await _queueManager.MarkFailedAsync(uploadItem.Id,
                    $"Only {blocksSent}/{totalBlockCount} blocks uploaded successfully", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            await _queueManager.MarkFailedAsync(uploadItem.Id, ex.Message, cancellationToken);
            _logger.LogError(ex, "Failed to process upload for {Path}", uploadItem.RelativePath);
        }
        finally
        {
            fileLock.Release();
        }
    }

    private async Task ProcessFileOperationQueue(CancellationToken cancellationToken)
    {
        var pendingOperations = await _queueManager.GetPendingFileOperationsAsync(cancellationToken);

        foreach (var operation in pendingOperations.Take(5)) // Process max 5 at a time
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await _queueManager.IncrementAttemptAsync(operation.Id, cancellationToken);

                var operationCommand = new
                {
                    OperationType = (int)operation.OperationType,
                    operation.RelativePath,
                    operation.OldRelativePath,
                    Timestamp = operation.OperationTimestamp
                };

                var json = JsonSerializer.Serialize(operationCommand, JsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_options.BridgeEndpoint}/operation", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    await _queueManager.MarkCompletedAsync(operation.Id, cancellationToken);
                    _logger.LogInformation("Completed file operation {Operation} for {Path}",
                        operation.OperationType, operation.RelativePath);
                }
                else
                {
                    await _queueManager.MarkFailedAsync(operation.Id,
                        $"HTTP {response.StatusCode}", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await _queueManager.MarkFailedAsync(operation.Id, ex.Message, cancellationToken);
                _logger.LogError(ex, "Failed to process file operation {Operation} for {Path}",
                    operation.OperationType, operation.RelativePath);
            }
        }
    }

    private async Task PerformMaintenance(CancellationToken cancellationToken)
    {
        try
        {
            // Cleanup old completed queue items
            await _queueManager.CleanupCompletedItemsAsync(TimeSpan.FromDays(7), cancellationToken);

            // Cleanup orphaned blocks
            await _blockStore.CleanupOrphanedBlocksAsync(cancellationToken);

            // Check storage limits and evict if necessary
            var storageSize = await _blockStore.GetStorageUsageAsync(cancellationToken);
            const long maxStorageSize = 1024L * 1024L * 1024L; // 1GB limit

            if (storageSize > maxStorageSize)
            {
                var targetSize = (long)(maxStorageSize * 0.7); // Reduce to 70%
                await _blockStore.EvictLeastRecentlyUsedAsync(targetSize, cancellationToken);
                _logger.LogInformation("Evicted cache blocks to reduce storage from {OldSize} to target {TargetSize}",
                    storageSize, targetSize);
            }

            _logger.LogDebug("Completed maintenance operations");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to perform some maintenance operations");
        }
    }

    #region Helper Methods (unchanged from original)

    private bool ShouldSkipDueToDebouncing(FileOperationEvent operation)
    {
        if (_lastProcessedTimes.TryGetValue(operation.RelativePath, out var lastTime))
        {
            var timeSinceLastProcess = DateTime.UtcNow - lastTime;
            var debounceTime = TimeSpan.FromMilliseconds(_options.FileChangeDelayMs * 3);

            if (timeSinceLastProcess < debounceTime)
            {
                _logger.LogDebug("Debouncing {OperationType} for {Path} (last processed {TimeSince}ms ago)",
                    operation.OperationType, operation.RelativePath, timeSinceLastProcess.TotalMilliseconds);
                return true;
            }
        }

        if (_lastProcessedTimes.Count % 100 == 0)
        {
            CleanupOldEntries();
        }

        return false;
    }

    private void CleanupOldEntries()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var toRemove = _lastProcessedTimes
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _lastProcessedTimes.TryRemove(key, out _);
        }

        _logger.LogDebug("Cleaned up {Count} old debounce entries", toRemove.Count);
    }

    private async Task<T?> RetryFileOperation<T>(Func<T> operation, string operationName, CancellationToken cancellationToken) where T : class
    {
        const int maxAttempts = 3;
        const int baseDelayMs = 100;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return operation();
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                _logger.LogDebug("Attempt {Attempt}/{MaxAttempts} failed for {Operation}: {Error}",
                    attempt, maxAttempts, operationName, ex.Message);

                var delay = baseDelayMs * attempt;
                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger.LogError("All {MaxAttempts} attempts failed for {Operation}", maxAttempts, operationName);
        return null;
    }

    private async Task<bool> WaitForFileToBeReady(string filePath, CancellationToken cancellationToken)
    {
        const int maxAttempts = 50;
        const int delayMs = 100;
        const int stableCheckCount = 3;

        var stableChecks = 0;
        var lastSize = -1L;
        var lastWriteTime = DateTime.MinValue;

        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                var fileInfo = new FileInfo(filePath);

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var currentSize = stream.Length;
                    var currentWriteTime = fileInfo.LastWriteTimeUtc;

                    if (currentSize == lastSize && currentWriteTime == lastWriteTime && currentSize > 0)
                    {
                        stableChecks++;
                        if (stableChecks >= stableCheckCount)
                        {
                            _logger.LogDebug("File is ready after {Attempts} attempts: {FilePath} (Size: {Size} bytes)",
                                i + 1, filePath, currentSize);
                            return true;
                        }
                    }
                    else
                    {
                        stableChecks = 0;
                        lastSize = currentSize;
                        lastWriteTime = currentWriteTime;
                    }
                }
            }
            catch (IOException)
            {
                stableChecks = 0;
                if (cancellationToken.IsCancellationRequested)
                    return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning("Access denied to file: {FilePath} - {Error}", filePath, ex.Message);
                return false;
            }

            await Task.Delay(delayMs, cancellationToken);
        }

        _logger.LogWarning("File not ready after {MaxAttempts} attempts: {FilePath}", maxAttempts, filePath);
        return false;
    }

    private static string CalculateManifestHash(FileManifest manifest)
    {
        var combined = $"{manifest.RelativePath}|{manifest.FileSize}|{string.Join(",", manifest.Blocks.Select(b => b.Hash))}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(combined));
    }

    private static string ComputeBlockHash(byte[] data)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(data)).ToLowerInvariant();
    }

    #endregion

    public void Dispose()
    {
        _queueProcessorTimer?.Dispose();
        foreach (var semaphore in _fileLocks.Values)
        {
            semaphore.Dispose();
        }
        _fileLocks.Clear();
    }
}