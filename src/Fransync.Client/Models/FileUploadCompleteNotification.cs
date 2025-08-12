namespace Fransync.Client.Models;

public class FileUploadCompleteNotification
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int BlockCount { get; set; }
    public DateTime CompletedAt { get; set; }
}