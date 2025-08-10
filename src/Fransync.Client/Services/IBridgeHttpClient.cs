using Fransync.Client.Models;

namespace Fransync.Client.Services;

public interface IBridgeHttpClient
{
    Task<bool> SendManifestAsync(FileManifest manifest, CancellationToken cancellationToken = default);
    Task<bool> SendBlockAsync(string relativePath, int index, string hash, byte[] data, CancellationToken cancellationToken = default);
}