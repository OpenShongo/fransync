namespace Fransync.Client.Models;

public class PendingFileDownload
{
    public string RelativePath { get; set; } = string.Empty;
    public int ExpectedBlockCount { get; set; }
    public HashSet<int> ReceivedBlocks { get; set; } = new();
    public DateTime ManifestReceivedAt { get; set; }
    public bool DownloadTriggered { get; set; }
}