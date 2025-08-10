using Fransync.Client.Models;

namespace Fransync.Client.Services;

public interface IDirectorySnapshotService
{
    Task<DirectorySnapshot> CreateSnapshotAsync(string directoryPath, CancellationToken cancellationToken = default);
    Task<DirectorySnapshot?> GetRemoteSnapshotAsync(CancellationToken cancellationToken = default);
    DirectoryComparisonResult CompareSnapshots(DirectorySnapshot? remote, DirectorySnapshot local);
    Task<string> CalculateFileHashAsync(string filePath, CancellationToken cancellationToken = default);
    Task<DirectorySnapshot?> GetSourceSnapshotAsync(CancellationToken cancellationToken = default);
    Task<DirectorySnapshot?> GetRemoteSnapshotForManifestGenerationAsync(CancellationToken cancellationToken = default);
}