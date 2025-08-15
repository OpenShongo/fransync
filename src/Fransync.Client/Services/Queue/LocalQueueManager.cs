using System.Collections.Concurrent;
using System.Text.Json;
using Fransync.Client.Models.Queue;
using Microsoft.Extensions.Logging;

namespace Fransync.Client.Services.Queue;

public class LocalQueueManager : ILocalQueueManager
{
    private readonly string _queueBasePath;
    private readonly ILogger<LocalQueueManager> _logger;
    private readonly ConcurrentDictionary<Guid, QueueItemBase> _memoryCache = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _fileOperationSemaphore = new(1, 1);

    public LocalQueueManager(string basePath, ILogger<LocalQueueManager> logger)
    {
        _queueBasePath = Path.Combine(basePath, ".fransync", "queue");
        _logger = logger;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        EnsureDirectoryStructure();
        LoadExistingQueueItems();
    }

    private void EnsureDirectoryStructure()
    {
        var directories = new[]
        {
            Path.Combine(_queueBasePath, "upload"),
            Path.Combine(_queueBasePath, "download"),
            Path.Combine(_queueBasePath, "operations"),
            Path.Combine(_queueBasePath, "completed"),
            Path.Combine(_queueBasePath, "failed")
        };

        foreach (var dir in directories)
        {
            Directory.CreateDirectory(dir);
        }
    }

    private void LoadExistingQueueItems()
    {
        try
        {
            var queueDirs = new[]
            {
                ("upload", typeof(UploadQueueItem)),
                ("download", typeof(DownloadQueueItem)),
                ("operations", typeof(FileOperationQueueItem))
            };

            foreach (var (subDir, itemType) in queueDirs)
            {
                var dirPath = Path.Combine(_queueBasePath, subDir);
                if (!Directory.Exists(dirPath)) continue;

                foreach (var file in Directory.GetFiles(dirPath, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var item = (QueueItemBase?)JsonSerializer.Deserialize(json, itemType, _jsonOptions);
                        if (item != null)
                        {
                            _memoryCache[item.Id] = item;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load queue item from {File}", file);
                    }
                }
            }

            _logger.LogInformation("Loaded {Count} existing queue items", _memoryCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load existing queue items");
        }
    }

    public async Task EnqueueUploadAsync(UploadQueueItem item, CancellationToken cancellationToken = default)
    {
        await EnqueueItemAsync(item, "upload", cancellationToken);
    }

    public async Task EnqueueDownloadAsync(DownloadQueueItem item, CancellationToken cancellationToken = default)
    {
        await EnqueueItemAsync(item, "download", cancellationToken);
    }

    public async Task EnqueueFileOperationAsync(FileOperationQueueItem item, CancellationToken cancellationToken = default)
    {
        await EnqueueItemAsync(item, "operations", cancellationToken);
    }

    private async Task EnqueueItemAsync(QueueItemBase item, string subDirectory, CancellationToken cancellationToken)
    {
        await _fileOperationSemaphore.WaitAsync(cancellationToken);
        try
        {
            var filePath = Path.Combine(_queueBasePath, subDirectory, $"{item.Id}.json");
            var json = JsonSerializer.Serialize(item, item.GetType(), _jsonOptions);

            await File.WriteAllTextAsync(filePath, json, cancellationToken);
            _memoryCache[item.Id] = item;

            _logger.LogDebug("Enqueued {ItemType} item {ItemId} for {Path}",
                item.Type, item.Id, item.RelativePath);
        }
        finally
        {
            _fileOperationSemaphore.Release();
        }
    }

    public async Task<IEnumerable<UploadQueueItem>> GetPendingUploadsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(
            _memoryCache.Values
                .OfType<UploadQueueItem>()
                .Where(item => item.Status == QueueItemStatus.Pending || item.Status == QueueItemStatus.Retrying)
                .OrderBy(item => item.CreatedAt)
                .ToList()
        );
    }

    public async Task<IEnumerable<DownloadQueueItem>> GetPendingDownloadsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(
            _memoryCache.Values
                .OfType<DownloadQueueItem>()
                .Where(item => item.Status == QueueItemStatus.Pending || item.Status == QueueItemStatus.Retrying)
                .OrderBy(item => item.CreatedAt)
                .ToList()
        );
    }

    public async Task<IEnumerable<FileOperationQueueItem>> GetPendingFileOperationsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(
            _memoryCache.Values
                .OfType<FileOperationQueueItem>()
                .Where(item => item.Status == QueueItemStatus.Pending || item.Status == QueueItemStatus.Retrying)
                .OrderBy(item => item.CreatedAt)
                .ToList()
        );
    }

    public async Task<UploadQueueItem?> GetUploadAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(_memoryCache.TryGetValue(itemId, out var item) ? item as UploadQueueItem : null);
    }

    public async Task<DownloadQueueItem?> GetDownloadAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(_memoryCache.TryGetValue(itemId, out var item) ? item as DownloadQueueItem : null);
    }

    public async Task UpdateStatusAsync(Guid itemId, QueueItemStatus status, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        if (!_memoryCache.TryGetValue(itemId, out var item)) return;

        item.Status = status;
        item.LastAttemptAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(errorMessage))
        {
            item.ErrorMessage = errorMessage;
        }

        await PersistItemAsync(item, cancellationToken);
    }

    public async Task MarkCompletedAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        if (!_memoryCache.TryGetValue(itemId, out var item)) return;

        item.Status = QueueItemStatus.Completed;
        item.LastAttemptAt = DateTime.UtcNow;

        // Move to completed directory
        await MoveItemToDirectory(item, "completed", cancellationToken);
    }

    public async Task MarkFailedAsync(Guid itemId, string errorMessage, CancellationToken cancellationToken = default)
    {
        if (!_memoryCache.TryGetValue(itemId, out var item)) return;

        item.Status = QueueItemStatus.Failed;
        item.ErrorMessage = errorMessage;
        item.LastAttemptAt = DateTime.UtcNow;

        if (item.AttemptCount < item.MaxAttempts)
        {
            item.Status = QueueItemStatus.Retrying;
            await PersistItemAsync(item, cancellationToken);
        }
        else
        {
            await MoveItemToDirectory(item, "failed", cancellationToken);
        }
    }

    public async Task IncrementAttemptAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        if (!_memoryCache.TryGetValue(itemId, out var item)) return;

        item.AttemptCount++;
        item.Status = QueueItemStatus.Processing;
        item.LastAttemptAt = DateTime.UtcNow;

        await PersistItemAsync(item, cancellationToken);
    }

    public async Task UpdateUploadProgressAsync(Guid itemId, int completedBlocks, CancellationToken cancellationToken = default)
    {
        if (_memoryCache.TryGetValue(itemId, out var item) && item is UploadQueueItem uploadItem)
        {
            if (!uploadItem.CompletedBlocks.Contains(completedBlocks))
            {
                uploadItem.CompletedBlocks.Add(completedBlocks);
                await PersistItemAsync(uploadItem, cancellationToken);
            }
        }
    }

    public async Task UpdateDownloadProgressAsync(Guid itemId, int downloadedBlocks, CancellationToken cancellationToken = default)
    {
        if (_memoryCache.TryGetValue(itemId, out var item) && item is DownloadQueueItem downloadItem)
        {
            if (!downloadItem.DownloadedBlocks.Contains(downloadedBlocks))
            {
                downloadItem.DownloadedBlocks.Add(downloadedBlocks);
                await PersistItemAsync(downloadItem, cancellationToken);
            }
        }
    }

    public async Task<int> GetQueueCountAsync(QueueItemType type, CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(
            _memoryCache.Values.Count(item => item.Type == type &&
                (item.Status == QueueItemStatus.Pending || item.Status == QueueItemStatus.Processing))
        );
    }

    public async Task<IEnumerable<QueueItemBase>> GetFailedItemsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(
            _memoryCache.Values.Where(item => item.Status == QueueItemStatus.Failed).ToList()
        );
    }

    public async Task CleanupCompletedItemsAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var cutoffTime = DateTime.UtcNow - olderThan;
        var completedDir = Path.Combine(_queueBasePath, "completed");

        if (!Directory.Exists(completedDir)) return;

        var filesToDelete = Directory.GetFiles(completedDir, "*.json")
            .Where(file => File.GetCreationTimeUtc(file) < cutoffTime)
            .ToList();

        foreach (var file in filesToDelete)
        {
            try
            {
                File.Delete(file);
                _logger.LogDebug("Cleaned up old completed queue item: {File}", Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete old queue item: {File}", file);
            }
        }

        if (filesToDelete.Any())
        {
            _logger.LogInformation("Cleaned up {Count} old completed queue items", filesToDelete.Count);
        }
    }

    private async Task PersistItemAsync(QueueItemBase item, CancellationToken cancellationToken)
    {
        await _fileOperationSemaphore.WaitAsync(cancellationToken);
        try
        {
            var subDir = GetSubDirectoryForType(item.Type);
            var filePath = Path.Combine(_queueBasePath, subDir, $"{item.Id}.json");
            var json = JsonSerializer.Serialize(item, item.GetType(), _jsonOptions);

            await File.WriteAllTextAsync(filePath, json, cancellationToken);
        }
        finally
        {
            _fileOperationSemaphore.Release();
        }
    }

    private async Task MoveItemToDirectory(QueueItemBase item, string targetDirectory, CancellationToken cancellationToken)
    {
        await _fileOperationSemaphore.WaitAsync(cancellationToken);
        try
        {
            var sourceSubDir = GetSubDirectoryForType(item.Type);
            var sourceFile = Path.Combine(_queueBasePath, sourceSubDir, $"{item.Id}.json");
            var targetFile = Path.Combine(_queueBasePath, targetDirectory, $"{item.Id}.json");

            if (File.Exists(sourceFile))
            {
                var json = JsonSerializer.Serialize(item, item.GetType(), _jsonOptions);
                await File.WriteAllTextAsync(targetFile, json, cancellationToken);
                File.Delete(sourceFile);
            }

            if (targetDirectory == "completed" || targetDirectory == "failed")
            {
                _memoryCache.TryRemove(item.Id, out _);
            }
        }
        finally
        {
            _fileOperationSemaphore.Release();
        }
    }

    private static string GetSubDirectoryForType(QueueItemType type)
    {
        return type switch
        {
            QueueItemType.Upload => "upload",
            QueueItemType.Download => "download",
            QueueItemType.FileOperation => "operations",
            _ => "misc"
        };
    }
}