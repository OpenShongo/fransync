namespace Fransync.Client.Models;

public class ManifestNotification
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int BlockCount { get; set; }
}
