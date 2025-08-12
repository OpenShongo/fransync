using Fransync.Client.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Fransync.Client.Services;

public class WebSocketMessageHandler : IWebSocketMessageHandler
{
    private readonly IFileDownloadService _downloadService;
    private readonly ILogger<WebSocketMessageHandler> _logger;
    private readonly ConcurrentDictionary<string, PendingFileDownload> _pendingDownloads = new();

    public WebSocketMessageHandler(
        IFileDownloadService downloadService,
        ILogger<WebSocketMessageHandler> logger)
    {
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

            // Create or update pending download tracking
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
                        _logger.LogWarning("Timeout waiting for upload completion for {RelativePath}, downloading anyway ({ReceivedBlocks}/{ExpectedBlocks} blocks)",
                            manifestNotification.RelativePath, pending.ReceivedBlocks.Count, pending.ExpectedBlockCount);
                        await TriggerDownload(manifestNotification.RelativePath, cancellationToken);
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

            // Track block completion
            if (_pendingDownloads.TryGetValue(blockNotification.FileId, out var pendingDownload))
            {
                pendingDownload.ReceivedBlocks.Add(blockNotification.BlockIndex);

                _logger.LogDebug("Block progress for {FileId}: {ReceivedBlocks}/{ExpectedBlocks}",
                    blockNotification.FileId, pendingDownload.ReceivedBlocks.Count, pendingDownload.ExpectedBlockCount);

                // Note: We no longer trigger download here directly, instead we wait for file_upload_complete
                // This prevents the race condition more reliably
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

            // This is the definitive signal that all blocks are available
            await TriggerDownload(completionNotification.RelativePath, cancellationToken);
        }
    }

    private async Task TriggerDownload(string relativePath, CancellationToken cancellationToken)
    {
        if (_pendingDownloads.TryGetValue(relativePath, out var pendingDownload))
        {
            if (pendingDownload.DownloadTriggered)
            {
                _logger.LogDebug("Download already triggered for {RelativePath}", relativePath);
                return;
            }

            pendingDownload.DownloadTriggered = true;

            // Small delay to ensure all blocks are fully stored
            await Task.Delay(150, cancellationToken);

            var success = await _downloadService.DownloadFileAsync(relativePath, cancellationToken);

            if (success)
            {
                _logger.LogInformation("Successfully downloaded {RelativePath}", relativePath);
            }
            else
            {
                _logger.LogWarning("Failed to download {RelativePath}", relativePath);

                // Mark as not triggered so retry mechanisms can work
                pendingDownload.DownloadTriggered = false;
            }

            // Clean up tracking only on success
            if (success)
            {
                _pendingDownloads.TryRemove(relativePath, out _);
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
                break;

            case FileOperationType.Renamed:
                await HandleFileRename(fileOperation, cancellationToken);
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

        var renamed = await _downloadService.RenameFileAsync(operation.OldRelativePath, operation.RelativePath, cancellationToken);

        if (!renamed)
        {
            _logger.LogInformation("Source file not found for rename, downloading new file: {RelativePath}", operation.RelativePath);
            await _downloadService.DownloadFileAsync(operation.RelativePath, cancellationToken);
        }
    }
}