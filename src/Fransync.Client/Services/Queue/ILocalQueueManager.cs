using Fransync.Client.Models.Queue;

namespace Fransync.Client.Services.Queue;

public interface ILocalQueueManager
{
    // Upload queue operations
    Task EnqueueUploadAsync(UploadQueueItem item, CancellationToken cancellationToken = default);
    Task<IEnumerable<UploadQueueItem>> GetPendingUploadsAsync(CancellationToken cancellationToken = default);
    Task<UploadQueueItem?> GetUploadAsync(Guid itemId, CancellationToken cancellationToken = default);

    // Download queue operations
    Task EnqueueDownloadAsync(DownloadQueueItem item, CancellationToken cancellationToken = default);
    Task<IEnumerable<DownloadQueueItem>> GetPendingDownloadsAsync(CancellationToken cancellationToken = default);
    Task<DownloadQueueItem?> GetDownloadAsync(Guid itemId, CancellationToken cancellationToken = default);

    // File operation queue
    Task EnqueueFileOperationAsync(FileOperationQueueItem item, CancellationToken cancellationToken = default);
    Task<IEnumerable<FileOperationQueueItem>> GetPendingFileOperationsAsync(CancellationToken cancellationToken = default);

    // Status management
    Task UpdateStatusAsync(Guid itemId, QueueItemStatus status, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task MarkCompletedAsync(Guid itemId, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(Guid itemId, string errorMessage, CancellationToken cancellationToken = default);
    Task IncrementAttemptAsync(Guid itemId, CancellationToken cancellationToken = default);

    // Queue management
    Task<int> GetQueueCountAsync(QueueItemType type, CancellationToken cancellationToken = default);
    Task CleanupCompletedItemsAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);
    Task<IEnumerable<QueueItemBase>> GetFailedItemsAsync(CancellationToken cancellationToken = default);

    // Progress tracking
    Task UpdateUploadProgressAsync(Guid itemId, int blockIndex, CancellationToken cancellationToken = default);
    Task UpdateDownloadProgressAsync(Guid itemId, int blockIndex, CancellationToken cancellationToken = default);

}