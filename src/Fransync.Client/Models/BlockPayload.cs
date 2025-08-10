namespace Fransync.Client.Models;

public class BlockPayload
{
    public string FileId { get; set; }
    public int BlockIndex { get; set; }
    public string Hash { get; set; }
    public string Data { get; set; }
}