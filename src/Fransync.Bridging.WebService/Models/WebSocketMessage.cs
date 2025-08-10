namespace Fransync.Bridging.WebService.Models;

public class WebSocketMessage
{
    public string Type { get; set; }
    public object Data { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class ManifestNotification
{
    public string RelativePath { get; set; }
    public string FileName { get; set; }
    public long FileSize { get; set; }
    public int BlockCount { get; set; }
}

public class BlockNotification
{
    public string FileId { get; set; }
    public int BlockIndex { get; set; }
    public string Hash { get; set; }
    public int Size { get; set; }
}
