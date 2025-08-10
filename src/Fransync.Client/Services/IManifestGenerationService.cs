using Fransync.Client.Models;

namespace Fransync.Client.Services;

public interface IManifestGenerationService
{
    Task<ManifestGenerationResult> GenerateManifestAsync(string relativePath, string sourcePath, CancellationToken cancellationToken = default);
    Task<List<ManifestGenerationResult>> GenerateManifestsForSnapshotAsync(DirectorySnapshot snapshot, CancellationToken cancellationToken = default);
    Task<bool> UploadManifestAndBlocksAsync(FileManifest manifest, string sourcePath, CancellationToken cancellationToken = default);
}