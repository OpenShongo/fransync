namespace Fransync.Bridging.WebService.Models;

public class DirectorySnapshot
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public Dictionary<string, FileSnapshot> Files { get; set; } = new();
    public string DirectoryHash { get; set; } = string.Empty;
}