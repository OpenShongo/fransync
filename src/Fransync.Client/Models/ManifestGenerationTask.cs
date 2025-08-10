namespace Fransync.Client.Models;

public class ManifestGenerationTask
{
    public string RelativePath { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public ManifestTaskStatus Status { get; set; } = ManifestTaskStatus.Queued;
    public string? ErrorMessage { get; set; }
}

public enum ManifestTaskStatus
{
    Queued,
    Processing,
    Completed,
    Failed
}

public class ManifestGenerationResult
{
    public bool Success { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public FileManifest? Manifest { get; set; }
}