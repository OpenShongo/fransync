namespace Fransync.Client.Models.Queue;

public class FileOperationQueueItem : QueueItemBase
{
    public FileOperationQueueItem()
    {
        Type = QueueItemType.FileOperation;
    }

    public FileOperationType OperationType { get; set; }
    public string? OldRelativePath { get; set; }
    public DateTime OperationTimestamp { get; set; }
}