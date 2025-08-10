using Fransync.Client.Models;

namespace Fransync.Client.Services;

public interface IFileSyncService
{
    Task ProcessFileOperationAsync(FileOperationEvent operation, CancellationToken cancellationToken = default);
    Task RegisterDirectoryStructureAsync(CancellationToken cancellationToken = default);
}