using System.Security.Cryptography;

namespace Fransync.Client.Services.Storage;

public interface ILocalBlockStore
{
    // Block storage operations
    Task StoreBlockAsync(string fileId, int blockIndex, byte[] data, CancellationToken cancellationToken = default);
    Task<byte[]?> GetBlockAsync(string fileId, int blockIndex, CancellationToken cancellationToken = default);
    Task<bool> HasBlockAsync(string fileId, int blockIndex, CancellationToken cancellationToken = default);
    Task DeleteBlocksAsync(string fileId, CancellationToken cancellationToken = default);

    // Content-addressed operations (deduplication)
    Task<string> StoreBlockByHashAsync(byte[] data, CancellationToken cancellationToken = default);
    Task<byte[]?> GetBlockByHashAsync(string blockHash, CancellationToken cancellationToken = default);
    Task<bool> HasBlockByHashAsync(string blockHash, CancellationToken cancellationToken = default);

    // File assembly operations
    Task<byte[]?> AssembleFileAsync(string fileId, int totalBlocks, CancellationToken cancellationToken = default);
    Task<bool> CanAssembleFileAsync(string fileId, int totalBlocks, CancellationToken cancellationToken = default);

    // Storage management
    Task<long> GetStorageUsageAsync(CancellationToken cancellationToken = default);
    Task<int> GetBlockCountAsync(CancellationToken cancellationToken = default);
    Task CleanupOrphanedBlocksAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<string>> GetMissingBlocksAsync(string fileId, int totalBlocks, CancellationToken cancellationToken = default);

    // Cache management
    Task EvictLeastRecentlyUsedAsync(long targetSizeBytes, CancellationToken cancellationToken = default);
    Task<IEnumerable<string>> GetCachedBlockHashesAsync(CancellationToken cancellationToken = default);
}