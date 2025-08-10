namespace Fransync.Client.Models;

public class DirectoryComparisonResult
{
    public List<string> FilesToDownload { get; set; } = new();
    public List<string> FilesToUpdate { get; set; } = new();
    public List<string> FilesToDelete { get; set; } = new();
    public List<(string oldPath, string newPath)> FilesToRename { get; set; } = new();
    public bool RequiresSync => FilesToDownload.Count > 0 || FilesToUpdate.Count > 0 || FilesToDelete.Count > 0;
}