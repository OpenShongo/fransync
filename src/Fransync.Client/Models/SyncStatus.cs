namespace Fransync.Client.Models
{
    public class SyncStatus
    {
        public int PendingUploads { get; set; }
        public int PendingDownloads { get; set; }
        public int PendingOperations { get; set; }
        public long TotalBytesUploaded { get; set; }
        public long TotalBytesDownloaded { get; set; }
        public DateTime LastActivity { get; set; }
        public bool IsHealthy { get; set; }
        public List<string> Issues { get; set; } = new();
    }

    public class StorageInfo
    {
        public long BlockCacheSize { get; set; }
        public int BlockCount { get; set; }
        public long QueueSize { get; set; } // ADD this missing property
        public double DiskUsagePercent { get; set; }
        public TimeSpan CacheRetention { get; set; }
    }

    public class SyncProgressEvent
    {
        public string Operation { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int Progress { get; set; }
        public int Total { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class SyncErrorEvent
    {
        public string Operation { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public Exception? Exception { get; set; }
        public bool IsRetryable { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}