using Fransync.Client.Configuration;
using Fransync.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fransync.Client.Services;

public class FileDownloadService(
    IOptions<SyncClientOptions> options,
    HttpClient httpClient,
    ILogger<FileDownloadService> logger)
    : IFileDownloadService
{
    private readonly SyncClientOptions _options = options.Value;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<bool> DownloadFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 5;
        const int retryDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var result = await DownloadFileInternalAsync(relativePath, cancellationToken);
                if (result)
                {
                    return true;
                }

                if (attempt < maxRetries)
                {
                    logger.LogInformation("Download attempt {Attempt}/{MaxRetries} failed for {RelativePath}, retrying in {Delay}ms",
                        attempt, maxRetries, relativePath, retryDelayMs * attempt);
                    await Task.Delay(retryDelayMs * attempt, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download attempt {Attempt} failed for {RelativePath}", attempt, relativePath);
                if (attempt == maxRetries)
                {
                    throw;
                }
                await Task.Delay(retryDelayMs * attempt, cancellationToken);
            }
        }

        logger.LogError("Failed to download {RelativePath} after {MaxRetries} attempts", relativePath, maxRetries);
        return false;
    }

    private async Task<bool> DownloadFileInternalAsync(string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            // Get manifest
            var manifestResponse = await httpClient.GetAsync(
                $"{_options.BridgeEndpoint}/manifest/{Uri.EscapeDataString(relativePath)}", cancellationToken);

            if (!manifestResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to get manifest for {RelativePath}: {StatusCode}",
                    relativePath, manifestResponse.StatusCode);
                return false;
            }

            var manifestJson = await manifestResponse.Content.ReadAsStringAsync(cancellationToken);
            logger.LogDebug("Received manifest JSON: {Json}", manifestJson);

            var manifest = JsonSerializer.Deserialize<FileManifest>(manifestJson, JsonOptions);

            if (manifest == null)
            {
                logger.LogError("Failed to deserialize manifest for {RelativePath}", relativePath);
                return false;
            }

            // Create target file path
            var targetPath = Path.Combine(_options.SyncWithPath, relativePath);
            var targetDir = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // Check if file needs updating
            if (await ShouldSkipDownload(targetPath, manifest, cancellationToken))
            {
                logger.LogDebug("File is already up to date: {RelativePath}", relativePath);
                return true;
            }

            // Download and assemble blocks
            var fileBlocks = new byte[manifest.Blocks.Count][];

            for (int i = 0; i < manifest.Blocks.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) return false;

                var blockResponse = await httpClient.GetAsync(
                    $"{_options.BridgeEndpoint}/block/{i}/{Uri.EscapeDataString(relativePath)}",
                    cancellationToken);

                if (!blockResponse.IsSuccessStatusCode)
                {
                    logger.LogError("Failed to download block {BlockIndex} for {RelativePath}: {StatusCode}",
                        i, relativePath, blockResponse.StatusCode);
                    return false;
                }

                var blockJson = await blockResponse.Content.ReadAsStringAsync(cancellationToken);
                var blockPayload = JsonSerializer.Deserialize<BlockPayload>(blockJson, JsonOptions);

                if (blockPayload?.Data == null)
                {
                    logger.LogError("Failed to deserialize block {BlockIndex} for {RelativePath}", i, relativePath);
                    return false;
                }

                fileBlocks[i] = Convert.FromBase64String(blockPayload.Data);
                logger.LogDebug("Downloaded block {BlockIndex}/{TotalBlocks} for {RelativePath}",
                    i + 1, manifest.Blocks.Count, relativePath);
            }

            // Write file (this will overwrite existing files)
            var tempPath = targetPath + ".tmp";
            try
            {
                using (var fileStream = File.Create(tempPath))
                {
                    foreach (var block in fileBlocks)
                    {
                        await fileStream.WriteAsync(block, cancellationToken);
                    }
                }

                // Atomic replace - move temp file to final location
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                File.Move(tempPath, targetPath);

                logger.LogInformation("Successfully downloaded/updated file: {RelativePath} -> {TargetPath}",
                    relativePath, targetPath);
                return true;
            }
            catch
            {
                // Clean up temp file on error
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error downloading file: {RelativePath}", relativePath);
            return false;
        }
    }

    private async Task<bool> ShouldSkipDownload(string targetPath, FileManifest manifest, CancellationToken cancellationToken)
    {
        if (!File.Exists(targetPath))
        {
            return false; // File doesn't exist, need to download
        }

        try
        {
            var fileInfo = new FileInfo(targetPath);

            // Quick check - if size is different, file has changed
            if (fileInfo.Length != manifest.FileSize)
            {
                logger.LogDebug("File size changed, will update: {TargetPath}", targetPath);
                return false;
            }

            // If sizes match, check a few block hashes to verify content
            // (Checking all blocks would be expensive, so we check a sample)
            var blocksToCheck = Math.Min(3, manifest.Blocks.Count);
            using var fileStream = File.OpenRead(targetPath);

            for (int i = 0; i < blocksToCheck; i++)
            {
                var blockInfo = manifest.Blocks[i];
                var buffer = new byte[blockInfo.Size];
                fileStream.Seek(i * manifest.BlockSize, SeekOrigin.Begin);
                var bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, blockInfo.Size), cancellationToken);

                if (bytesRead != blockInfo.Size)
                {
                    return false; // Size mismatch
                }

                var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, bytesRead)));
                if (!string.Equals(hash, blockInfo.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogDebug("Block hash mismatch at block {BlockIndex}, will update: {TargetPath}", i, targetPath);
                    return false; // Content has changed
                }
            }

            return true; // File appears to be the same
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error checking file status, will download: {TargetPath}", targetPath);
            return false; // When in doubt, download
        }
    }

    public async Task SyncAllFilesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var manifestsResponse = await httpClient.GetAsync(
                $"{_options.BridgeEndpoint}/manifests", cancellationToken);

            if (!manifestsResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to get manifest list: {StatusCode}", manifestsResponse.StatusCode);
                return;
            }

            var manifestsJson = await manifestsResponse.Content.ReadAsStringAsync(cancellationToken);
            var relativePaths = JsonSerializer.Deserialize<string[]>(manifestsJson, JsonOptions);

            if (relativePaths == null)
            {
                logger.LogWarning("No manifests found or failed to deserialize manifest list");
                return;
            }

            logger.LogInformation("Starting sync of {FileCount} files", relativePaths.Length);

            foreach (var relativePath in relativePaths)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await DownloadFileAsync(relativePath, cancellationToken);
            }

            logger.LogInformation("Completed sync of all files");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during full sync");
        }
    }

    public async Task<bool> DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var targetPath = Path.Combine(_options.SyncWithPath, relativePath);

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
                logger.LogInformation("Successfully deleted file: {RelativePath}", relativePath);

                var directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    logger.LogDebug("Removed empty directory: {Directory}", directory);
                }

                return true;
            }

            logger.LogWarning("File not found for deletion: {RelativePath}", relativePath);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting file: {RelativePath}", relativePath);
            return false;
        }
    }

    public async Task<bool> RenameFileAsync(string oldRelativePath, string newRelativePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var oldTargetPath = Path.Combine(_options.SyncWithPath, oldRelativePath);
            var newTargetPath = Path.Combine(_options.SyncWithPath, newRelativePath);

            if (!File.Exists(oldTargetPath))
            {
                logger.LogWarning("Source file not found for rename: {OldPath}", oldRelativePath);
                return false;
            }

            var newDirectory = Path.GetDirectoryName(newTargetPath);
            if (!string.IsNullOrEmpty(newDirectory) && !Directory.Exists(newDirectory))
            {
                Directory.CreateDirectory(newDirectory);
            }

            // Handle case where target file already exists
            if (File.Exists(newTargetPath))
            {
                File.Delete(newTargetPath);
            }

            File.Move(oldTargetPath, newTargetPath);
            logger.LogInformation("Successfully renamed file: {OldPath} -> {NewPath}", oldRelativePath, newRelativePath);

            var oldDirectory = Path.GetDirectoryName(oldTargetPath);
            if (!string.IsNullOrEmpty(oldDirectory) && Directory.Exists(oldDirectory) && !Directory.EnumerateFileSystemEntries(oldDirectory).Any())
            {
                Directory.Delete(oldDirectory);
                logger.LogDebug("Removed empty directory: {Directory}", oldDirectory);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error renaming file: {OldPath} -> {NewPath}", oldRelativePath, newRelativePath);
            return false;
        }
    }

    public async Task<bool> CheckManifestExistsAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var manifestResponse = await httpClient.GetAsync(
                $"{_options.BridgeEndpoint}/manifest/{Uri.EscapeDataString(relativePath)}", cancellationToken);

            return manifestResponse.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}