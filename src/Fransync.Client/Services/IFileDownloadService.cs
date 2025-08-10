using Fransync.Client.Models;

namespace Fransync.Client.Services;

public interface IFileDownloadService
{
    Task<bool> DownloadFileAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<bool> CheckManifestExistsAsync(string relativePath, CancellationToken cancellationToken = default);
    Task SyncAllFilesAsync(CancellationToken cancellationToken = default);
    Task<bool> DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<bool> RenameFileAsync(string oldRelativePath, string newRelativePath, CancellationToken cancellationToken = default);
}