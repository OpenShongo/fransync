namespace Fransync.Client.Models;

public class BlockNotification
{
    public string FileId { get; set; } = string.Empty;
    public int BlockIndex { get; set; }
    public string Hash { get; set; } = string.Empty;
    public int Size { get; set; }
}