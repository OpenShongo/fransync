using Fransync.Bridging.WebService.Models;

namespace Fransync.Bridging.WebService.Services;

public interface ISyncStore
{
    void StoreManifest(FileManifest manifest);
    void StoreBlock(BlockPayload payload);
    FileManifest? GetManifest(string relativePath);
    byte[]? GetBlock(string fileId, int blockIndex);
    IEnumerable<string> GetAllManifestPaths();
    bool DeleteManifest(string relativePath);
    bool RenameManifest(string oldRelativePath, string newRelativePath);

    void StoreDirectorySnapshot(DirectorySnapshot snapshot);
    DirectorySnapshot? GetSourceDirectorySnapshot();
    void MarkFileAsDeleted(string relativePath);
    IEnumerable<string> GetDeletedFiles();

    // Enhanced block completion tracking
    bool IsFileComplete(string relativePath);
    int GetReceivedBlockCount(string relativePath);
    bool HasAllBlocksReceived(string relativePath);
}