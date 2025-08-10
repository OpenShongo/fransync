using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Fransync.Client.Configuration;
using Fransync.Client.Models;

namespace Fransync.Client.Services;

public class SyncClientHostedService(
    IFileWatcherService fileWatcherService,
    IBridgeWebSocketClient webSocketClient,
    IFileSyncService fileSyncService,
    IFileDownloadService downloadService,
    IDirectorySnapshotService snapshotService,
    IManifestGenerationService manifestGenerationService,
    IWebSocketMessageHandler messageHandler,
    IOptions<SyncClientOptions> options,
    ILogger<SyncClientHostedService> logger)
    : BackgroundService
{
    private readonly SyncClientOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Sync Client Service starting...");

        // Setup event handlers
        fileWatcherService.FileOperationDetected += async (operation) =>
        {
            await fileSyncService.ProcessFileOperationAsync(operation, stoppingToken);
        };

        webSocketClient.MessageReceived += async (message) =>
        {
            await messageHandler.HandleMessageAsync(message, stoppingToken);
        };

        webSocketClient.ConnectionStatusChanged += (status) =>
        {
            logger.LogInformation("WebSocket connection status: {Status}", status);

            if (status == "Connected")
            {
                // Perform startup sync when connected
                _ = Task.Run(async () =>
                {
                    await PerformStartupSyncAsync(stoppingToken);
                }, stoppingToken);
            }
        };

        try
        {
            // Start file watching
            fileWatcherService.StartWatching();
            logger.LogInformation("File watching started");

            // Connect with retry policy
            _ = Task.Run(async () =>
            {
                await webSocketClient.ConnectWithRetryAsync(stoppingToken);
            }, stoppingToken);

            logger.LogInformation("Sync Client Service started successfully");
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Sync Client Service stopping...");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in Sync Client Service");
        }
    }

    private async Task PerformStartupSyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Starting startup synchronization...");

            // Create local snapshot of the monitored directory (source)
            var localMonitoredSnapshot = await snapshotService.CreateSnapshotAsync(_options.MonitoredPath, cancellationToken);

            // Create local snapshot of sync destination
            var localSyncSnapshot = await snapshotService.CreateSnapshotAsync(_options.SyncWithPath, cancellationToken);

            // Register our directory structure with the bridge if we have files
            if (localMonitoredSnapshot.TotalFiles > 0)
            {
                await fileSyncService.RegisterDirectoryStructureAsync(cancellationToken);
                logger.LogInformation("Registered {FileCount} files with bridge server", localMonitoredSnapshot.TotalFiles);
            }

            // Get the source snapshot from bridge server
            var remoteSourceSnapshot = await snapshotService.GetRemoteSnapshotForManifestGenerationAsync(cancellationToken);

            if (remoteSourceSnapshot == null)
            {
                logger.LogInformation("No remote source found, this client will serve as the source");

                // Generate manifests for our monitored directory
                if (localMonitoredSnapshot.TotalFiles > 0)
                {
                    await GenerateAndUploadManifestsAsync(localMonitoredSnapshot, cancellationToken);
                }
                return;
            }

            // Check if we need to generate missing manifests
            var missingManifests = await IdentifyMissingManifestsAsync(remoteSourceSnapshot, cancellationToken);

            if (missingManifests.Count > 0)
            {
                logger.LogInformation("Found {Count} files without manifests, generating them", missingManifests.Count);
                await GenerateManifestsForMissingFilesAsync(missingManifests, cancellationToken);

                // Wait a moment for manifest upload to complete
                await Task.Delay(2000, cancellationToken);
            }

            // Now perform the regular sync comparison
            var comparison = snapshotService.CompareSnapshots(remoteSourceSnapshot, localSyncSnapshot);

            if (!comparison.RequiresSync)
            {
                logger.LogInformation("No startup sync required - directories are in sync");
                return;
            }

            logger.LogInformation("Startup sync required: {Download} downloads, {Update} updates",
                comparison.FilesToDownload.Count, comparison.FilesToUpdate.Count);

            // Download missing files (now manifests should exist)
            await DownloadFilesWithRetryAsync(comparison.FilesToDownload, "missing files", cancellationToken);

            // Update changed files
            await DownloadFilesWithRetryAsync(comparison.FilesToUpdate, "changed files", cancellationToken);

            logger.LogInformation("Startup synchronization completed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during startup synchronization");
        }
    }

    private async Task GenerateAndUploadManifestsAsync(DirectorySnapshot snapshot, CancellationToken cancellationToken)
    {
        logger.LogInformation("Generating and uploading manifests for {FileCount} files", snapshot.TotalFiles);

        var manifestResults = await manifestGenerationService.GenerateManifestsForSnapshotAsync(snapshot, cancellationToken);

        foreach (var result in manifestResults.Where(r => r.Success && r.Manifest != null))
        {
            if (cancellationToken.IsCancellationRequested) break;

            var sourcePath = Path.Combine(_options.MonitoredPath, result.RelativePath);
            await manifestGenerationService.UploadManifestAndBlocksAsync(result.Manifest!, sourcePath, cancellationToken);
        }

        var successful = manifestResults.Count(r => r.Success);
        logger.LogInformation("Manifest generation completed: {Successful}/{Total} successful", successful, manifestResults.Count);
    }

    private async Task<List<string>> IdentifyMissingManifestsAsync(DirectorySnapshot sourceSnapshot, CancellationToken cancellationToken)
    {
        var missingManifests = new List<string>();

        foreach (var file in sourceSnapshot.Files.Keys)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                // Check if manifest exists on bridge server
                var manifestResponse = await downloadService.CheckManifestExistsAsync(file, cancellationToken);
                if (!manifestResponse)
                {
                    missingManifests.Add(file);
                }
            }
            catch
            {
                // If we can't check, assume it's missing
                missingManifests.Add(file);
            }
        }

        return missingManifests;
    }

    private async Task GenerateManifestsForMissingFilesAsync(List<string> missingFiles, CancellationToken cancellationToken)
    {
        var semaphore = new SemaphoreSlim(3, 3); // Limit concurrent manifest generation

        var tasks = missingFiles.Select(async filePath =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var sourcePath = Path.Combine(_options.MonitoredPath, filePath);
                if (File.Exists(sourcePath))
                {
                    var result = await manifestGenerationService.GenerateManifestAsync(filePath, sourcePath, cancellationToken);
                    if (result.Success && result.Manifest != null)
                    {
                        await manifestGenerationService.UploadManifestAndBlocksAsync(result.Manifest, sourcePath, cancellationToken);
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task DownloadFilesWithRetryAsync(List<string> filePaths, string description, CancellationToken cancellationToken)
    {
        foreach (var filePath in filePaths)
        {
            if (cancellationToken.IsCancellationRequested) break;

            logger.LogDebug("Downloading {Description}: {FilePath}", description, filePath);

            // Retry download with exponential backoff
            var maxRetries = 3;
            var delay = 1000;

            for (int retry = 0; retry < maxRetries; retry++)
            {
                var success = await downloadService.DownloadFileAsync(filePath, cancellationToken);
                if (success)
                {
                    break;
                }

                if (retry < maxRetries - 1)
                {
                    logger.LogWarning("Download failed for {FilePath}, retrying in {Delay}ms (attempt {Retry}/{MaxRetries})",
                        filePath, delay, retry + 1, maxRetries);
                    await Task.Delay(delay, cancellationToken);
                    delay *= 2; // Exponential backoff
                }
                else
                {
                    logger.LogError("Failed to download {FilePath} after {MaxRetries} attempts", filePath, maxRetries);
                }
            }

            await Task.Delay(100, cancellationToken); // Small delay between downloads
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Sync Client Service stopping...");

        fileWatcherService.StopWatching();
        await webSocketClient.DisconnectAsync(cancellationToken);

        await base.StopAsync(cancellationToken);
        logger.LogInformation("Sync Client Service stopped");
    }
}