namespace Fransync.Client.Models
{

    public class FileManifest
    {
        public string FileName { get; set; }
        public string RelativePath { get; set; }
        public long FileSize { get; set; }
        public int BlockSize { get; set; }
        public List<BlockInfo> Blocks { get; set; } = new();
    }
}
