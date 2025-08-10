using Fransync.Client.Models;

namespace Fransync.Client.Services;

public interface IFileWatcherService
{
    event Func<FileOperationEvent, Task> FileOperationDetected;
    void StartWatching();
    void StopWatching();
    void Dispose();
}