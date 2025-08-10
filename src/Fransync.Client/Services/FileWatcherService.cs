using Fransync.Client.Configuration;
using Fransync.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Fransync.Client.Services;

public class FileWatcherService : IFileWatcherService, IDisposable
{
    private readonly SyncClientOptions _options;
    private readonly ILogger<FileWatcherService> _logger;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _lastEventTimes = new();
    private readonly ConcurrentDictionary<string, FileInfo> _lastFileStates = new();

    public event Func<FileOperationEvent, Task>? FileOperationDetected;

    public FileWatcherService(IOptions<SyncClientOptions> options, ILogger<FileWatcherService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void StartWatching()
    {
        if (!Directory.Exists(_options.MonitoredPath))
        {
            Directory.CreateDirectory(_options.MonitoredPath);
            _logger.LogInformation("Created monitored directory: {Path}", _options.MonitoredPath);
        }

        _watcher = new FileSystemWatcher(_options.MonitoredPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 65536 // Increased buffer size
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileCreated;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnError;

        _watcher.EnableRaisingEvents = true;
        _logger.LogInformation("Started watching folder: {Path}", _options.MonitoredPath);
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
            _logger.LogInformation("Stopped watching folder");
        }
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnoreEvent(e.FullPath) || !File.Exists(e.FullPath))
            return;

        // Check if file actually changed (not just accessed)
        if (!HasFileActuallyChanged(e.FullPath))
            return;

        var relativePath = Path.GetRelativePath(_options.MonitoredPath, e.FullPath);
        var operation = new FileOperationEvent
        {
            OperationType = FileOperationType.Modified,
            RelativePath = relativePath
        };

        _logger.LogDebug("File changed detected: {RelativePath}", relativePath);
        await InvokeFileOperationDetected(operation);
    }

    private async void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnoreEvent(e.FullPath))
            return;

        // Small delay to let file creation complete
        await Task.Delay(100);

        if (!File.Exists(e.FullPath))
            return;

        var relativePath = Path.GetRelativePath(_options.MonitoredPath, e.FullPath);
        var operation = new FileOperationEvent
        {
            OperationType = FileOperationType.Created,
            RelativePath = relativePath
        };

        _logger.LogDebug("File created detected: {RelativePath}", relativePath);
        await InvokeFileOperationDetected(operation);
    }

    private async void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnoreEvent(e.FullPath))
            return;

        var relativePath = Path.GetRelativePath(_options.MonitoredPath, e.FullPath);
        var operation = new FileOperationEvent
        {
            OperationType = FileOperationType.Deleted,
            RelativePath = relativePath
        };

        _logger.LogDebug("File deleted detected: {RelativePath}", relativePath);
        await InvokeFileOperationDetected(operation);
    }

    private async void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnoreEvent(e.FullPath))
            return;

        var newRelativePath = Path.GetRelativePath(_options.MonitoredPath, e.FullPath);
        var oldRelativePath = Path.GetRelativePath(_options.MonitoredPath, e.OldFullPath);

        var operation = new FileOperationEvent
        {
            OperationType = FileOperationType.Renamed,
            RelativePath = newRelativePath,
            OldRelativePath = oldRelativePath
        };

        _logger.LogDebug("File renamed detected: {OldPath} -> {NewPath}", oldRelativePath, newRelativePath);
        await InvokeFileOperationDetected(operation);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error occurred");
    }

    private bool ShouldIgnoreEvent(string fullPath)
    {
        // Ignore temporary files, system files, and hidden files
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(fileName) ||
            fileName.StartsWith('.') ||
            fileName.StartsWith('~') ||
            fileName.EndsWith(".tmp") ||
            fileName.EndsWith(".swp") ||
            fileName.EndsWith(".bak") ||
            fileName.Contains("~$") ||
            fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Enhanced debounce for rapid events (common with drag-and-drop)
        var now = DateTime.UtcNow;
        if (_lastEventTimes.TryGetValue(fullPath, out var lastTime))
        {
            var timeSinceLastEvent = now - lastTime;

            // Longer debounce for recently seen files (helps with drag-and-drop)
            var debounceTime = timeSinceLastEvent.TotalSeconds < 5 ? 500 : 200; // 500ms if seen recently, 200ms otherwise

            if (timeSinceLastEvent.TotalMilliseconds < debounceTime)
            {
                return true;
            }
        }

        _lastEventTimes[fullPath] = now;

        // Periodic cleanup
        if (_lastEventTimes.Count % 50 == 0)
        {
            CleanupOldEventTimes();
        }

        return false;
    }

    private bool HasFileActuallyChanged(string fullPath)
    {
        try
        {
            var currentInfo = new FileInfo(fullPath);
            if (!currentInfo.Exists)
                return false;

            var key = fullPath;
            if (_lastFileStates.TryGetValue(key, out var lastInfo))
            {
                // For large files, be more conservative about change detection
                bool sizeChanged = lastInfo.Length != currentInfo.Length;
                bool timeChanged = Math.Abs((lastInfo.LastWriteTime - currentInfo.LastWriteTime).TotalMilliseconds) > 100;

                bool changed = sizeChanged || timeChanged;

                if (changed && currentInfo.Length > 100 * 1024) // Large file
                {
                    _logger.LogDebug("Large file change detected: {Path} (Size: {OldSize} -> {NewSize}, Time: {OldTime} -> {NewTime})",
                        fullPath, lastInfo.Length, currentInfo.Length, lastInfo.LastWriteTime, currentInfo.LastWriteTime);
                }

                _lastFileStates[key] = currentInfo;
                return changed;
            }
            else
            {
                // First time seeing this file
                _lastFileStates[key] = currentInfo;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error checking file state for {Path}: {Error}", fullPath, ex.Message);
            return true; // When in doubt, assume it changed
        }
    }

    private void CleanupOldEventTimes()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        var toRemove = _lastEventTimes.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();

        foreach (var key in toRemove)
        {
            _lastEventTimes.TryRemove(key, out _);
            _lastFileStates.TryRemove(key, out _);
        }

        if (toRemove.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} old file watcher entries", toRemove.Count);
        }
    }

    private async Task InvokeFileOperationDetected(FileOperationEvent operation)
    {
        try
        {
            if (FileOperationDetected != null)
            {
                await FileOperationDetected.Invoke(operation);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file operation: {OperationType} for {RelativePath}",
                operation.OperationType, operation.RelativePath);
        }
    }

    public void Dispose()
    {
        StopWatching();
    }
}