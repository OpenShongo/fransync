using Fransync.Client.Models;

namespace Fransync.Client.Services;

public interface IBridgeHttpClient
{
    Task<bool> SendManifestAsync(FileManifest manifest, CancellationToken cancellationToken = default);
    Task<bool> SendBlockAsync(string relativePath, int index, string hash, byte[] data, CancellationToken cancellationToken = default);

    Task<FileManifest?> GetManifestAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<byte[]?> GetBlockAsync(string relativePath, int blockIndex, CancellationToken cancellationToken = default);
}