using System.Text.Json.Serialization;

namespace Fransync.Client.Models;

public class DirectorySnapshot
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, FileSnapshot> Files { get; set; } = new();

    [JsonPropertyName("directoryHash")]
    public string DirectoryHash { get; set; } = string.Empty;
}