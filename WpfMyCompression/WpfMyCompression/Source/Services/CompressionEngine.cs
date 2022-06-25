using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommonLib.Source.Common.Utils;

namespace WpfMyCompression.Source.Services
{
    public class CompressionEngine
    {
        public int ChunkSize { get; }

        public CompressionEngine(int chunkSize = 64)
        {
            ChunkSize = chunkSize;
        }

        public async Task<byte[]> Compress(string filePath)
        {
            var chunk = await FileUtils.ReadBytesAsync(filePath, 0, ChunkSize);
            return chunk;
        }
    }
}
