using Fransync.Client.Models;
using Fransync.Client.Models.Queue;

public interface IFileSyncService : IDisposable
{
    // Core file operation processing (backward compatible)
    Task ProcessFileOperationAsync(FileOperationEvent operation, CancellationToken cancellationToken = default);
    Task RegisterDirectoryStructureAsync(CancellationToken cancellationToken = default);

    // Enhanced file operation methods (decentralized architecture)
    Task ProcessFileCreatedAsync(string filePath, CancellationToken cancellationToken = default);
    Task ProcessFileModifiedAsync(string filePath, CancellationToken cancellationToken = default);
    Task ProcessFileDeletedAsync(string filePath, CancellationToken cancellationToken = default);
    Task ProcessFileRenamedAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);

    // Queue processing capabilities
    Task ProcessUploadQueueAsync(CancellationToken cancellationToken = default);
    Task ProcessDownloadQueueAsync(CancellationToken cancellationToken = default);
    Task ProcessFileOperationQueueAsync(CancellationToken cancellationToken = default);

    // Download orchestration
    Task InitiateFileDownloadAsync(string relativePath, string manifestHash, CancellationToken cancellationToken = default);
    Task HandleIncomingManifestAsync(ManifestNotification notification, CancellationToken cancellationToken = default);
    Task HandleFileUploadCompleteAsync(FileUploadCompleteNotification notification, CancellationToken cancellationToken = default);

    // Status and monitoring
    Task<SyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<QueueItemBase>> GetQueueStatusAsync(CancellationToken cancellationToken = default);
    Task<StorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken = default);

    // Maintenance operations
    Task PerformMaintenanceAsync(CancellationToken cancellationToken = default);
    Task ForceResyncAsync(string? specificPath = null, CancellationToken cancellationToken = default);

    // Events for progress monitoring and error handling
    event Func<SyncProgressEvent, Task>? SyncProgressChanged;
    event Func<SyncErrorEvent, Task>? SyncErrorOccurred;
}