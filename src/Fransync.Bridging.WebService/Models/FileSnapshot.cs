namespace Fransync.Bridging.WebService.Models;

public class FileSnapshot
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public int BlockCount { get; set; }
}