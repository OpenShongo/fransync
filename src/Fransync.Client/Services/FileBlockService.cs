using System.Security.Cryptography;

namespace Fransync.Client.Services;

public class FileBlockService : IFileBlockService
{
    public IEnumerable<(int Index, byte[] Block, string Hash)> SplitFileIntoBlocks(string filePath, int blockSize)
    {
        using var stream = File.OpenRead(filePath);
        int index = 0;
        var buffer = new byte[blockSize];
        int bytesRead;

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var block = buffer.Take(bytesRead).ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(block));
            yield return (index++, block, hash);
        }
    }
}