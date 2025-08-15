using Fransync.Client.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Fransync.Client.Services;

public class WebSocketMessageHandler : IWebSocketMessageHandler
{
    private readonly IFileSyncService _fileSyncService;
    private readonly IFileDownloadService _downloadService; // Keep for backward compatibility
    private readonly ILogger<WebSocketMessageHandler> _logger;
    private readonly ConcurrentDictionary<string, PendingFileDownload> _pendingDownloads = new();

    public WebSocketMessageHandler(
        IFileSyncService fileSyncService,
        IFileDownloadService downloadService,
        ILogger<WebSocketMessageHandler> logger)
    {
        _fileSyncService = fileSyncService;
        _downloadService = downloadService;
        _logger = logger;
    }

    public async Task HandleMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        try
        {
            var wsMessage = JsonSerializer.Deserialize<WebSocketMessage>(message);
            if (wsMessage?.Type == null) return;

            switch (wsMessage.Type.ToLowerInvariant())
            {
                case "manifest_received":
                    await HandleManifestReceived(wsMessage, cancellationToken);
                    break;

                case "block_received":
                    await HandleBlockReceived(wsMessage, cancellationToken);
                    break;

                case "file_upload_complete":
                    await HandleFileUploadComplete(wsMessage, cancellationToken);
                    break;

                case "file_operation":
                    await HandleFileOperation(wsMessage, cancellationToken);
                    break;

                default:
                    _logger.LogDebug("Unknown message type: {MessageType}", wsMessage.Type);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse WebSocket message: {Message}", message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling WebSocket message");
        }
    }

    private async Task HandleManifestReceived(WebSocketMessage message, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(message.Data);
        var manifestNotification = JsonSerializer.Deserialize<ManifestNotification>(data);

        if (manifestNotification?.RelativePath != null)
        {
            _logger.LogInformation("New manifest available: {RelativePath} ({BlockCount} blocks expected)",
                manifestNotification.RelativePath, manifestNotification.BlockCount);

            // Use the new decentralized FileSyncService
            await _fileSyncService.HandleIncomingManifestAsync(manifestNotification, cancellationToken);

            // Create or update pending download tracking for monitoring
            var pendingDownload = new PendingFileDownload
            {
                RelativePath = manifestNotification.RelativePath,
                ExpectedBlockCount = manifestNotification.BlockCount,
                ReceivedBlocks = new HashSet<int>(),
                ManifestReceivedAt = DateTime.UtcNow
            };

            _pendingDownloads[manifestNotification.RelativePath] = pendingDownload;

            // If there are no blocks expected (empty file), download will be triggered by file_upload_complete
            if (manifestNotification.BlockCount == 0)
            {
                _logger.LogDebug("Empty file manifest received for {RelativePath}, waiting for completion notification",
                    manifestNotification.RelativePath);
            }
            else
            {
                // Set a longer timeout to download anyway if completion notification doesn't arrive
                _ = Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ContinueWith(async _ =>
                {
                    if (_pendingDownloads.TryGetValue(manifestNotification.RelativePath, out var pending) &&
                        !pending.DownloadTriggered)
                    {
                        _logger.LogWarning("Timeout waiting for upload completion for {RelativePath}, initiating download anyway ({ReceivedBlocks}/{ExpectedBlocks} blocks)",
                            manifestNotification.RelativePath, pending.ReceivedBlocks.Count, pending.ExpectedBlockCount);

                        // Generate a fallback manifest hash
                        var fallbackHash = $"manifest_{manifestNotification.RelativePath}_{DateTime.UtcNow.Ticks}";
                        await _fileSyncService.InitiateFileDownloadAsync(manifestNotification.RelativePath, fallbackHash, cancellationToken);
                        pending.DownloadTriggered = true;
                    }
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }
    }

    private async Task HandleBlockReceived(WebSocketMessage message, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(message.Data);
        var blockNotification = JsonSerializer.Deserialize<BlockNotification>(data);

        if (blockNotification?.FileId != null)
        {
            _logger.LogDebug("Block received for file: {FileId}, Block: {BlockIndex}",
                blockNotification.FileId, blockNotification.BlockIndex);

            // Track block completion for monitoring
            if (_pendingDownloads.TryGetValue(blockNotification.FileId, out var pendingDownload))
            {
                pendingDownload.ReceivedBlocks.Add(blockNotification.BlockIndex);

                _logger.LogDebug("Block progress for {FileId}: {ReceivedBlocks}/{ExpectedBlocks}",
                    blockNotification.FileId, pendingDownload.ReceivedBlocks.Count, pendingDownload.ExpectedBlockCount);

                // Note: We no longer trigger download here directly, instead we wait for file_upload_complete
                // The decentralized system will handle block downloading through the queue system
            }
        }
    }

    private async Task HandleFileUploadComplete(WebSocketMessage message, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(message.Data);
        var completionNotification = JsonSerializer.Deserialize<FileUploadCompleteNotification>(data);

        if (completionNotification?.RelativePath != null)
        {
            _logger.LogInformation("File upload completed: {RelativePath} ({BlockCount} blocks)",
                completionNotification.RelativePath, completionNotification.BlockCount);

            // Use the new decentralized FileSyncService to handle upload completion
            await _fileSyncService.HandleFileUploadCompleteAsync(completionNotification, cancellationToken);

            // Update pending download tracking
            if (_pendingDownloads.TryGetValue(completionNotification.RelativePath, out var pendingDownload))
            {
                pendingDownload.DownloadTriggered = true;

                // Clean up tracking after a delay to allow the download queue to process
                _ = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ContinueWith(b =>
                {
                    _pendingDownloads.TryRemove(completionNotification.RelativePath, out var c);
                    _logger.LogDebug("Cleaned up pending download tracking for {RelativePath}", completionNotification.RelativePath);
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }
    }

    private async Task HandleFileOperation(WebSocketMessage message, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(message.Data);
        var fileOperation = JsonSerializer.Deserialize<FileOperationCommand>(data);

        if (fileOperation == null) return;

        switch (fileOperation.OperationType)
        {
            case FileOperationType.Deleted:
                _logger.LogInformation("File deletion requested: {RelativePath}", fileOperation.RelativePath);

                // Clean up any pending downloads for this file
                _pendingDownloads.TryRemove(fileOperation.RelativePath, out _);

                // Use decentralized system to handle deletion
                await _fileSyncService.ProcessFileDeletedAsync(fileOperation.RelativePath, cancellationToken);
                break;

            case FileOperationType.Renamed:
                await HandleFileRename(fileOperation, cancellationToken);
                break;

            case FileOperationType.Created:
                _logger.LogInformation("File creation notification received: {RelativePath}", fileOperation.RelativePath);
                // Creation is typically handled by manifest_received and file_upload_complete
                break;

            case FileOperationType.Modified:
                _logger.LogInformation("File modification notification received: {RelativePath}", fileOperation.RelativePath);
                // Modification is typically handled by manifest_received and file_upload_complete
                break;

            default:
                _logger.LogDebug("Unknown file operation type: {OperationType}", fileOperation.OperationType);
                break;
        }
    }

    private async Task HandleFileRename(FileOperationCommand operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(operation.OldRelativePath))
        {
            _logger.LogWarning("Rename operation missing old path: {NewPath}", operation.RelativePath);
            return;
        }

        _logger.LogInformation("File rename requested: {OldPath} -> {NewPath}",
            operation.OldRelativePath, operation.RelativePath);

        // Clean up pending downloads for old path
        _pendingDownloads.TryRemove(operation.OldRelativePath, out _);

        // Use decentralized system to handle rename
        await _fileSyncService.ProcessFileRenamedAsync(operation.OldRelativePath, operation.RelativePath, cancellationToken);

        // Fallback to direct download service for compatibility
        var renamed = await _downloadService.RenameFileAsync(operation.OldRelativePath, operation.RelativePath, cancellationToken);

        if (!renamed)
        {
            _logger.LogInformation("Source file not found for rename, will be handled by download queue: {RelativePath}", operation.RelativePath);
            // The FileSyncService.ProcessFileRenamedAsync already queues the new file for upload/download
        }
    }

    // Additional helper method to get current download status
    public async Task<Dictionary<string, object>> GetDownloadStatusAsync()
    {
        var status = new Dictionary<string, object>();

        foreach (var pending in _pendingDownloads)
        {
            status[pending.Key] = new
            {
                RelativePath = pending.Value.RelativePath,
                ExpectedBlocks = pending.Value.ExpectedBlockCount,
                ReceivedBlocks = pending.Value.ReceivedBlocks.Count,
                Progress = pending.Value.ExpectedBlockCount > 0
                    ? (double)pending.Value.ReceivedBlocks.Count / pending.Value.ExpectedBlockCount * 100
                    : 0,
                ManifestReceivedAt = pending.Value.ManifestReceivedAt,
                DownloadTriggered = pending.Value.DownloadTriggered
            };
        }

        // Also get queue status from FileSyncService
        try
        {
            var syncStatus = await _fileSyncService.GetSyncStatusAsync();
            status["_queueStatus"] = new
            {
                PendingUploads = syncStatus.PendingUploads,
                PendingDownloads = syncStatus.PendingDownloads,
                PendingOperations = syncStatus.PendingOperations,
                IsHealthy = syncStatus.IsHealthy,
                Issues = syncStatus.Issues
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get sync status");
        }

        return status;
    }

    // Cleanup method for periodic maintenance
    public async Task PerformMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30); // Clean up entries older than 30 minutes
        var toRemove = _pendingDownloads
            .Where(kvp => kvp.Value.ManifestReceivedAt < cutoff && kvp.Value.DownloadTriggered)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _pendingDownloads.TryRemove(key, out _);
        }

        if (toRemove.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} old pending download entries", toRemove.Count);
        }
        await Task.CompletedTask;
    }
}