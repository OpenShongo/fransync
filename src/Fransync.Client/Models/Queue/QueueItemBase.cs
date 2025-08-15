namespace Fransync.Client.Models.Queue;

public class QueueItemBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public QueueItemType Type { get; set; }
    public QueueItemStatus Status { get; set; } = QueueItemStatus.Pending;
    public string RelativePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptAt { get; set; }
    public int AttemptCount { get; set; } = 0;
    public int MaxAttempts { get; set; } = 3;
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}