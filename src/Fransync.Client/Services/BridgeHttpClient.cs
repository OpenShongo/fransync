using System.Text;
using System.Text.Json;
using Fransync.Client.Configuration;
using Fransync.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fransync.Client.Services;

public class BridgeHttpClient(
    HttpClient httpClient,
    IOptions<SyncClientOptions> options,
    ILogger<BridgeHttpClient> logger)
    : IBridgeHttpClient
{
    private readonly SyncClientOptions _options = options.Value;

    public async Task<bool> SendManifestAsync(FileManifest manifest, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(manifest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"{_options.BridgeEndpoint}/manifest", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("[MANIFEST SENT] {RelativePath}", manifest.RelativePath);
                return true;
            }

            logger.LogWarning("[MANIFEST FAILED] {RelativePath} | {StatusCode}", manifest.RelativePath, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[MANIFEST ERROR] {RelativePath}", manifest.RelativePath);
            return false;
        }
    }

    public async Task<bool> SendBlockAsync(string relativePath, int index, string hash, byte[] data, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new BlockPayload
            {
                FileId = relativePath,
                BlockIndex = index,
                Hash = hash,
                Data = Convert.ToBase64String(data)
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            logger.LogDebug("Sending block {Index} for {RelativePath}: {OriginalSize} bytes -> {EncodedSize} bytes -> {JsonSize} bytes",
                index, relativePath, data.Length, payload.Data.Length, json.Length);

            var response = await httpClient.PostAsync($"{_options.BridgeEndpoint}/block", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("[BLOCK SENT] {RelativePath} | Block {Index}", relativePath, index);
                return true;
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("[BLOCK FAILED] {RelativePath} | Block {Index} | {StatusCode} | {Error}",
                relativePath, index, response.StatusCode, errorContent);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BLOCK ERROR] {RelativePath} | Block {Index}", relativePath, index);
            return false;
        }
    }

    public async Task<FileManifest?> GetManifestAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        try
        {
            // URL encode the relative path to handle special characters
            var encodedPath = Uri.EscapeDataString(relativePath);
            var response = await httpClient.GetAsync($"{_options.BridgeEndpoint}/manifest/{encodedPath}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var manifest = JsonSerializer.Deserialize<FileManifest>(json);

                logger.LogInformation("[MANIFEST RETRIEVED] {RelativePath}", relativePath);
                return manifest;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogDebug("[MANIFEST NOT FOUND] {RelativePath}", relativePath);
                return null;
            }

            logger.LogWarning("[MANIFEST GET FAILED] {RelativePath} | {StatusCode}", relativePath, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[MANIFEST GET ERROR] {RelativePath}", relativePath);
            return null;
        }
    }

    public async Task<byte[]?> GetBlockAsync(string relativePath, int blockIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            // URL encode the relative path to handle special characters
            var encodedPath = Uri.EscapeDataString(relativePath);
            var response = await httpClient.GetAsync($"{_options.BridgeEndpoint}/block/{blockIndex}/{encodedPath}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var blockData = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                logger.LogInformation("[BLOCK RETRIEVED] {RelativePath} | Block {Index} | {Size} bytes",
                    relativePath, blockIndex, blockData.Length);
                return blockData;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogDebug("[BLOCK NOT FOUND] {RelativePath} | Block {Index}", relativePath, blockIndex);
                return null;
            }

            logger.LogWarning("[BLOCK GET FAILED] {RelativePath} | Block {Index} | {StatusCode}",
                relativePath, blockIndex, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BLOCK GET ERROR] {RelativePath} | Block {Index}", relativePath, blockIndex);
            return null;
        }
    }
}