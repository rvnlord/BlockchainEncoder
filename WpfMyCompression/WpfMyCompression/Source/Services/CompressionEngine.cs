using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommonLib.Source.Common.Converters;
using CommonLib.Source.Common.Extensions;
using CommonLib.Source.Common.Extensions.Collections;
using CommonLib.Source.Common.Utils;
using CommonLib.Source.Common.Utils.UtilClasses;

namespace WpfMyCompression.Source.Services
{
    public class CompressionEngine
    {
        private ILitecoinManager _lm;

        public ILitecoinManager Lm => _lm ??= new LitecoinManager();
        public int ChunkSize { get; }

        public CompressionEngine(int chunkSize = 64)
        {
            ChunkSize = chunkSize;
        }

        public async Task<byte[]> CompressAsync(string filePath)
        {
            var currentMapSize = long.MaxValue;
            long previousMapSize;
            var layer = 0;
            string mapFilePath;
            var fileSize = new FileInfo(filePath).Length;

            RemoveCompressedFiles(filePath);

            do
            {
                mapFilePath = await CompressLayerAsync(filePath, layer++, fileSize);

                previousMapSize = currentMapSize;
                currentMapSize = new FileInfo(mapFilePath).Length;

                filePath = mapFilePath;
            } 
            while (currentMapSize < previousMapSize);
            
            return await File.ReadAllBytesAsync(mapFilePath); // last mapPath can be loaded because file size shouldn't be big
        }

        private async Task<string> CompressLayerAsync(string filePath, int layer, long fileSize)
        {
            byte[] chunk;
            long offset = 0;
            string mapFilePath;

            do
            {
                chunk = await FileUtils.ReadBytesAsync(filePath, offset, ChunkSize);
              
                var match = await FindLongestMatchAsync(chunk);
                offset += match.ChunkLength; // `ChunkSize` would always give the initial size, `chunk.Length` would give initial size - 64 or truncated to the end of file, `match.ChunkLength` will always give the size truncated to the size to the actual match

                mapFilePath = await SaveToFileAsync(filePath, match, layer);

                await OnCompressionStatusChangingAsync(offset, fileSize, layer, "Compressing");
            } 
            while (chunk.Length > 0);


            return mapFilePath;
        }

        private async Task<RawBlockchainMatch> FindLongestMatchAsync(byte[] chunk)
        {
            var blockCount = await Lm.GetBlockCountAsync();
            var longestMatch = new RawBlockchainMatch();

            for (var i = 0; i < blockCount; i++)
            {
                var block = await Lm.GetBlockFromDbByIndex(i);
                var truncatedChunk = new List<byte>(chunk);
                var blockOffset = -1;
                var blockWithEntropy = block.RawData.Sha3(); // block.RawData.EncryptCamellia(block.RawData.Sha3());
                while (truncatedChunk.Count > 0 && (blockOffset = await blockWithEntropy.IndexOfAsync(truncatedChunk)) == -1)
                    truncatedChunk.RemoveLast();

                if (longestMatch.ChunkLength >= truncatedChunk.Count) 
                    continue;

                longestMatch.BlockNumber = i;
                longestMatch.BlockOffset = blockOffset;
                longestMatch.ChunkLength = truncatedChunk.Count;
            }

            return longestMatch;
        }

        private static async Task<string> SaveToFileAsync(string filePath, RawBlockchainMatch match, int layer)
        {
            var dir = Path.GetDirectoryName(filePath);
            var fileName = new FileInfo(filePath).Name.BeforeLastOrWhole(".");
            var mapFilePath = PathUtils.Combine(PathSeparator.BSlash, dir, $"{fileName}.L{layer}.lid");

            var bytesBlockNo = match.BlockNumber.ToByteArray().Take(3).ToArray();
            var bytesBlockOffset = match.BlockOffset.ToByteArray().Take(2).ToArray();
            var bytesChunkLength = match.ChunkLength.ToByteArray().Take(1).ToArray();

            await FileUtils.AppendAllBytesAsync(mapFilePath, bytesBlockNo.ConcatMany(bytesBlockOffset, bytesChunkLength).ToArray());
            return mapFilePath;
        }

        public void RemoveCompressedFiles(string filePath)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? throw new NullReferenceException());
            var fileName = new FileInfo(filePath).Name.BeforeLastOrWhole(".");

            foreach (var file in dir.EnumerateFiles($"{fileName}.L*.lid")) {
                file.Delete();
            }
        }
        
        private class RawBlockchainMatch
        {
            public int BlockNumber { get; set; }
            public int BlockOffset { get; set; }
            public int ChunkLength { get; set; }
        }

        public event MyAsyncEventHandler<CompressionEngine, CompressionStatusChangedEventArgs> CompressionStatusChanged;

        private async Task OnCompressionStatusChangingAsync(CompressionStatusChangedEventArgs e) => await CompressionStatusChanged.InvokeAsync(this, e);
        private async Task OnCompressionStatusChangingAsync(long fileOffset, long fileLength, int layer, string message) => await OnCompressionStatusChangingAsync(new CompressionStatusChangedEventArgs(fileOffset, fileLength, layer, message));

        public class CompressionStatusChangedEventArgs : EventArgs
        {
            public long FileOffset { get; }
            public long FileSize { get; }
            public int Layer { get; }
            public string Message { get; }

            public CompressionStatusChangedEventArgs(long fileOffset, long fileSize, int layer, string message)
            {
                FileOffset = fileOffset;
                FileSize = fileSize;
                Layer = layer;
                Message = message;
            }

            public override string ToString() => $"{Message} (O{FileOffset}/{FileSize}b L{Layer})";
        }
    }
}
