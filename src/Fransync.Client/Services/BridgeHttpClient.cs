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
}