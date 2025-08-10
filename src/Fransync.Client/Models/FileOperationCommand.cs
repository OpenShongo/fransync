namespace Fransync.Client.Models;

public class FileOperationCommand
{
    public FileOperationType OperationType { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string? OldRelativePath { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}