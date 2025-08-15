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

    public IEnumerable<byte[]> SplitIntoBlocks(byte[] fileData, int blockSize)
    {
        for (int offset = 0; offset < fileData.Length; offset += blockSize)
        {
            var size = Math.Min(blockSize, fileData.Length - offset);
            var block = new byte[size];
            Array.Copy(fileData, offset, block, 0, size);
            yield return block;
        }
    }
}