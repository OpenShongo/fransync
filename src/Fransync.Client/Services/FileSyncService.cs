using Fransync.Client.Configuration;
using Fransync.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Fransync.Client.Services;

public class FileSyncService(
    IOptions<SyncClientOptions> options,
    IFileBlockService fileBlockService,
    IBridgeHttpClient bridgeHttpClient,
    HttpClient httpClient,
    IDirectorySnapshotService snapshotService,
    ILogger<FileSyncService> logger)
    : IFileSyncService
{
    private readonly SyncClientOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, DateTime> _lastProcessedTimes = new();
    private readonly ConcurrentDictionary<string, Task> _activeOperations = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task ProcessFileOperationAsync(FileOperationEvent operation, CancellationToken cancellationToken = default)
    {
        var operationKey = $"{operation.OperationType}:{operation.RelativePath}";

        // Prevent concurrent operations on the same file
        if (_activeOperations.ContainsKey(operationKey))
        {
            logger.LogDebug("Operation already in progress for {Path}, skipping", operation.RelativePath);
            return;
        }

        // Debouncing: Skip if we recently processed this file
        if (ShouldSkipDueToDebouncing(operation))
        {
            logger.LogDebug("Skipping duplicate {OperationType} operation for {Path}",
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
            logger.LogInformation("Registering complete directory structure with bridge server");

            // Create snapshot of the monitored path (source)
            var snapshot = await snapshotService.CreateSnapshotAsync(_options.MonitoredPath, cancellationToken);

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"{_options.BridgeEndpoint}/register-directory", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Successfully registered directory structure: {FileCount} files", snapshot.TotalFiles);
            }
            else
            {
                logger.LogWarning("Failed to register directory structure: {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error registering directory structure");
        }
    }

    private async Task ProcessFileOperationInternalAsync(FileOperationEvent operation, CancellationToken cancellationToken)
    {
        try
        {
            // Initial delay to let rapid file changes settle
            await Task.Delay(_options.FileChangeDelayMs, cancellationToken);

            logger.LogInformation("Processing {OperationType} operation for {Path}",
                operation.OperationType, operation.RelativePath);

            switch (operation.OperationType)
            {
                case FileOperationType.Created:
                case FileOperationType.Modified:
                    await HandleFileUpload(operation.RelativePath, cancellationToken);
                    break;

                case FileOperationType.Deleted:
                    await HandleFileDelete(operation.RelativePath, cancellationToken);
                    break;

                case FileOperationType.Renamed:
                    await HandleFileRename(operation.OldRelativePath!, operation.RelativePath, cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("File operation cancelled: {OperationType} for {Path}",
                operation.OperationType, operation.RelativePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing file operation: {OperationType} for {Path}",
                operation.OperationType, operation.RelativePath);
        }
    }
    
    private bool ShouldSkipDueToDebouncing(FileOperationEvent operation)
    {
        if (_lastProcessedTimes.TryGetValue(operation.RelativePath, out var lastTime))
        {
            var timeSinceLastProcess = DateTime.UtcNow - lastTime;

            // Use longer debounce time for large files or when dropping files
            var debounceTime = TimeSpan.FromMilliseconds(_options.FileChangeDelayMs * 3); // Increased from 2

            if (timeSinceLastProcess < debounceTime)
            {
                logger.LogDebug("Debouncing {OperationType} for {Path} (last processed {TimeSince}ms ago)",
                    operation.OperationType, operation.RelativePath, timeSinceLastProcess.TotalMilliseconds);
                return true;
            }
        }

        // Clean up old entries
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

        logger.LogDebug("Cleaned up {Count} old debounce entries", toRemove.Count);
    }

    private async Task HandleFileUpload(string relativePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(_options.MonitoredPath, relativePath);

        if (!File.Exists(fullPath))
        {
            logger.LogDebug("File no longer exists: {Path}", relativePath);
            return;
        }

        // Additional delay for large files to ensure they're fully written
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > 100 * 1024) // Files larger than 100KB
        {
            var additionalDelay = Math.Min((int)(fileInfo.Length / (1024 * 1024)) * 500, 2000); // Up to 2 seconds for very large files
            logger.LogDebug("Large file detected ({Size} bytes), adding {Delay}ms delay", fileInfo.Length, additionalDelay);
            await Task.Delay(additionalDelay, cancellationToken);
        }

        // Wait for file to be fully written and available
        if (!await WaitForFileToBeReady(fullPath, cancellationToken))
        {
            logger.LogWarning("File was not ready for reading after waiting: {Path}", relativePath);
            return;
        }

        try
        {
            // Refresh file info after waiting
            fileInfo = new FileInfo(fullPath);
            logger.LogDebug("Processing file upload: {Path} (Size: {Size} bytes)", relativePath, fileInfo.Length);

            var manifest = new FileManifest
            {
                FileName = Path.GetFileName(fullPath),
                RelativePath = relativePath,
                FileSize = fileInfo.Length,
                BlockSize = _options.BlockSize
            };

            // Split file into blocks with retry for file access issues
            var blocks = await RetryFileOperation(
                () => fileBlockService.SplitFileIntoBlocks(fullPath, _options.BlockSize).ToList(),
                $"splitting file {relativePath}",
                cancellationToken);

            if (blocks == null)
            {
                logger.LogError("Failed to split file into blocks: {Path}", relativePath);
                return;
            }

            foreach (var (index, block, hash) in blocks)
            {
                manifest.Blocks.Add(new BlockInfo { Index = index, Hash = hash, Size = block.Length });
            }

            // Send manifest
            var manifestSent = await bridgeHttpClient.SendManifestAsync(manifest, cancellationToken);
            if (!manifestSent)
            {
                logger.LogError("Failed to send manifest for {Path}", relativePath);
                return;
            }

            // Send blocks with progress logging for large files
            int blocksSent = 0;
            foreach (var (index, block, hash) in blocks)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var blockSent = await bridgeHttpClient.SendBlockAsync(relativePath, index, hash, block, cancellationToken);
                if (blockSent)
                {
                    blocksSent++;

                    // Log progress for large files
                    if (blocks.Count > 5 && (blocksSent % Math.Max(1, blocks.Count / 5) == 0))
                    {
                        logger.LogDebug("Upload progress for {Path}: {Sent}/{Total} blocks",
                            relativePath, blocksSent, blocks.Count);
                    }
                }
                else
                {
                    logger.LogWarning("Failed to send block {Index} for {Path}", index, relativePath);
                }
            }

            if (blocksSent == blocks.Count)
            {
                logger.LogInformation("Successfully uploaded file: {Path} ({Size} bytes, {Blocks} blocks)",
                    relativePath, fileInfo.Length, blocks.Count);
            }
            else
            {
                logger.LogWarning("Partially uploaded file: {Path} ({Sent}/{Total} blocks)",
                    relativePath, blocksSent, blocks.Count);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "IO error while processing file {Path}, operation may be retried", relativePath);
            // Remove from debouncing cache so it can be retried
            _lastProcessedTimes.TryRemove(relativePath, out _);
            throw;
        }
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
                logger.LogDebug("Attempt {Attempt}/{MaxAttempts} failed for {Operation}: {Error}",
                    attempt, maxAttempts, operationName, ex.Message);

                var delay = baseDelayMs * attempt;
                await Task.Delay(delay, cancellationToken);
            }
        }

        logger.LogError("All {MaxAttempts} attempts failed for {Operation}", maxAttempts, operationName);
        return null;
    }

    // Enhanced WaitForFileToBeReady method with better large file handling
    private async Task<bool> WaitForFileToBeReady(string filePath, CancellationToken cancellationToken)
    {
        const int maxAttempts = 50; // Increased from 20
        const int delayMs = 100;    // Increased from 50
        const int stableCheckCount = 3; // Require file to be stable for 3 consecutive checks

        var stableChecks = 0;
        var lastSize = -1L;
        var lastWriteTime = DateTime.MinValue;

        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                // Check if file exists and is accessible
                if (!File.Exists(filePath))
                {
                    await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                var fileInfo = new FileInfo(filePath);

                // Try to open the file with shared read access to check if it's being written
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var currentSize = stream.Length;
                    var currentWriteTime = fileInfo.LastWriteTimeUtc;

                    // Check if file size and write time are stable
                    if (currentSize == lastSize && currentWriteTime == lastWriteTime && currentSize > 0)
                    {
                        stableChecks++;
                        if (stableChecks >= stableCheckCount)
                        {
                            logger.LogDebug("File is ready after {Attempts} attempts: {FilePath} (Size: {Size} bytes)",
                                i + 1, filePath, currentSize);
                            return true;
                        }
                    }
                    else
                    {
                        stableChecks = 0; // Reset stability counter
                        lastSize = currentSize;
                        lastWriteTime = currentWriteTime;

                        logger.LogDebug("File still changing: {FilePath} (Size: {Size}, WriteTime: {WriteTime})",
                            filePath, currentSize, currentWriteTime);
                    }
                }
            }
            catch (IOException ex)
            {
                // File is still being written to or locked
                logger.LogDebug("File access attempt {Attempt}: {FilePath} - {Error}", i + 1, filePath, ex.Message);
                stableChecks = 0;

                if (cancellationToken.IsCancellationRequested)
                    return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning("Access denied to file: {FilePath} - {Error}", filePath, ex.Message);
                return false;
            }

            await Task.Delay(delayMs, cancellationToken);
        }

        logger.LogWarning("File not ready after {MaxAttempts} attempts and {TotalTime}ms: {FilePath}",
            maxAttempts, maxAttempts * delayMs, filePath);
        return false;
    }

    private async Task HandleFileDelete(string relativePath, CancellationToken cancellationToken)
    {
        var operationCommand = new
        {
            OperationType = 3, // Deleted
            RelativePath = relativePath,
            Timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(operationCommand);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync($"{_options.BridgeEndpoint}/operation", content, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Successfully sent delete operation for: {Path}", relativePath);
        }
        else
        {
            logger.LogWarning("Failed to send delete operation for {Path}: {StatusCode}", relativePath, response.StatusCode);
        }
    }

    private async Task HandleFileRename(string oldRelativePath, string newRelativePath, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing rename: {OldPath} -> {NewPath}", oldRelativePath, newRelativePath);

        // First, send the rename operation command
        var operationCommand = new
        {
            OperationType = 2, // Renamed
            RelativePath = newRelativePath,
            OldRelativePath = oldRelativePath,
            Timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(operationCommand);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync($"{_options.BridgeEndpoint}/operation", content, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Successfully sent rename operation: {OldPath} -> {NewPath}", oldRelativePath, newRelativePath);
        }
        else
        {
            logger.LogWarning("Failed to send rename operation {OldPath} -> {NewPath}: {StatusCode}",
                oldRelativePath, newRelativePath, response.StatusCode);
        }

        // Always upload the file with the new name to ensure it exists in the destination
        await HandleFileUpload(newRelativePath, cancellationToken);
    }
}