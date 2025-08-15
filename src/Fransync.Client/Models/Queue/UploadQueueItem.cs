namespace Fransync.Client.Models.Queue;

public class UploadQueueItem : QueueItemBase
{
    public UploadQueueItem()
    {
        Type = QueueItemType.Upload;
    }

    public string FilePath { get; set; } = string.Empty;
    public FileManifest? Manifest { get; set; }
    public long FileSize { get; set; }
    public int BlockCount { get; set; }
    public List<int> CompletedBlocks { get; set; } = new();
    public string FileHash { get; set; } = string.Empty;
}