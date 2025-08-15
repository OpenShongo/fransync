using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fransync.Client.Services
{
    public interface IFileBlockService
    {
        IEnumerable<(int Index, byte[] Block, string Hash)> SplitFileIntoBlocks(string filePath, int blockSize);

        IEnumerable<byte[]> SplitIntoBlocks(byte[] fileData, int blockSize);
    }
}
