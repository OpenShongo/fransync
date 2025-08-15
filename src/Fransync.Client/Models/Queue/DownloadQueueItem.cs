namespace Fransync.Client.Models.Queue;

public class DownloadQueueItem : QueueItemBase
{
    public DownloadQueueItem()
    {
        Type = QueueItemType.Download;
    }

    public string DestinationPath { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public int ExpectedBlockCount { get; set; } // ADD this property
    public List<int> DownloadedBlocks { get; set; } = new();
    public long ExpectedFileSize { get; set; }
}