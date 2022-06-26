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
using MoreLinq;

namespace WpfMyCompression.Source.Services
{
    public class CompressionEngine
    {
        private ILitecoinManager _lm;
        private readonly List<byte[]> _expandedBlockHashes = new();

        public ILitecoinManager Lm => _lm ??= new LitecoinManager();
        public int ChunkSize { get; }

        public CompressionEngine(int chunkSize = 8)
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
            var maxForBits = BitUtils.MaxNumberStoredForBits(12);
            var originalFilePath = filePath;

            await OnCompressionStatusChangingAsync("Removing files");
            RemoveCompressedFiles(filePath);

            await OnCompressionStatusChangingAsync("Expanding hashes");
            await CreateExpandedBlockHashesAsync(maxForBits);

            do
            {
                mapFilePath = await CompressLayerAsync(filePath, layer++, fileSize);

                previousMapSize = currentMapSize;
                currentMapSize = new FileInfo(mapFilePath).Length;

                filePath = mapFilePath;
            } 
            while (currentMapSize < previousMapSize);
            
            PostProcessLayerFiles(originalFilePath, layer - 1);

            await OnCompressionStatusChangingAsync(fileSize, fileSize, layer - 1, "Compressed");

            return await File.ReadAllBytesAsync(mapFilePath); // last mapPath can be loaded because file size shouldn't be big
        }

        private async Task<string> CompressLayerAsync(string filePath, int layer, long fileSize)
        {
            byte[] chunk;
            long offset = 0;
            string mapFilePath;

            do
            {
                await OnCompressionStatusChangingAsync(offset, fileSize, layer, "Compressing");

                chunk = await FileUtils.ReadBytesAsync(filePath, offset, ChunkSize);
              
                //var match = await FindLongestMatchAsync(chunk);
                //offset += match.ChunkLength; // `ChunkSize` would always give the initial size, `chunk.Length` would give initial size - 64 or truncated to the end of file, `match.ChunkLength` will always give the size truncated to the size to the actual match
                var matches = await FindBestMatchesAsync(chunk);
                
                //mapFilePath = await SaveToFileAsync(filePath, match, layer);
                mapFilePath = await SaveToFileAsync(filePath, matches, layer, chunk);
                
                offset += matches?.Sum(m => m.ChunkLength) ?? ChunkSize;
            } 
            while (chunk.Length > 0);

            await PostProcessFlagsFileAsync(filePath, layer);
           
            await OnCompressionStatusChangingAsync(fileSize, fileSize, layer, $"L{layer} Compressed");

            return mapFilePath;
        }

        private static async Task PostProcessFlagsFileAsync(string filePath, int layer)
        {
            if (layer > 255)
                throw new ArgumentOutOfRangeException(null, @"Layer has to be lower than byte");

            var flagsFilePath = PathUtils.Combine(PathSeparator.BSlash, Path.GetDirectoryName(filePath), $"{new FileInfo(filePath).Name.BeforeLastOrWhole(".").BeforeLastOrWhole(".L")}.L{layer}.lid.flags");
            var flagsStr = await File.ReadAllTextAsync(flagsFilePath);
            var flagsBytes = flagsStr.BitArrayStringToBitArray().ToByteArray();
            await File.WriteAllBytesAsync(flagsFilePath, flagsBytes.Append_((byte)layer).ToArray());
        }

        private void PostProcessLayerFiles(string originalFilePath, int layer)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(originalFilePath) ?? throw new NullReferenceException());
            var fileName = new FileInfo(originalFilePath).Name.BeforeLastOrWhole(".");
            var compressedFiles = dir.EnumerateFiles($"{fileName}.L*.lid*").ToArray();
            var fileExcludingCurrentLayer = compressedFiles.Where(f => !f.Name.Between(".L", ".").Equals(layer.ToString())).ToArray();
            var currentLayerFiles = compressedFiles.Except(fileExcludingCurrentLayer).ToArray();

            foreach (var file in fileExcludingCurrentLayer) 
                file.Delete();

            foreach (var file in currentLayerFiles) 
                file.Rename($"{file.Name.BeforeLast(".L")}.lid{file.Name.AfterLastOrNull("lid")}");
        }

        private async Task<List<RawBlockchainMatch>> FindBestMatchesAsync(byte[] chunk)
        {
            var bestMatches = new List<RawBlockchainMatch>();
            
            for (var i = 0; i < _expandedBlockHashes.Count; i++)
            {
                var blockHash = _expandedBlockHashes[i];
                var batches = chunk.Batch(2).Select(b => b.Pad(2).ToArray()).ToArray();
                var matches = new List<RawBlockchainMatch>();

                foreach (var batch in batches)
                {
                    
                    var batchIndex = await blockHash.IndexOfAsync(batch);
                    if (batchIndex != -1)
                    {
                        matches.Add(new RawBlockchainMatch
                        {
                            BlockNumber = i,
                            BlockOffset = batchIndex,
                            ChunkLength = 2
                        });
                    }
                }

                if (matches.Count > bestMatches.Count)
                    bestMatches.ReplaceAll(matches);
                if (matches.Count == 4)
                    break;
            }

            return bestMatches.Count >= 4 ? bestMatches : throw new ArgumentOutOfRangeException(null, @"Can't find 3, 2 byte batches in a single block");
        }

        //private async Task<RawBlockchainMatch> FindLongestMatchAsync(byte[] chunk)
        //{
        //    var blockCount = await Lm.GetBlockCountAsync();
        //    var longestMatch = new RawBlockchainMatch();

        //    for (var i = 0; i < blockCount; i++)
        //    {
        //        var block = await Lm.GetBlockFromDbByIndex(i);
        //        var truncatedChunk = new List<byte>(chunk);
        //        var blockOffset = -1;
        //        var blockWithEntropy = block.RawData; //.Sha3(); // block.RawData.EncryptCamellia(block.RawData.Sha3());
        //        while (truncatedChunk.Count > 0 && (blockOffset = await blockWithEntropy.IndexOfAsync(truncatedChunk)) == -1)
        //            truncatedChunk.RemoveLast();

        //        if (longestMatch.ChunkLength >= truncatedChunk.Count) 
        //            continue;

        //        longestMatch.BlockNumber = i;
        //        longestMatch.BlockOffset = blockOffset;
        //        longestMatch.ChunkLength = truncatedChunk.Count;
        //    }

        //    return longestMatch;
        //}

        //private static async Task<string> SaveToFileAsync(string filePath, RawBlockchainMatch match, int layer)
        //{
        //    var dir = Path.GetDirectoryName(filePath);
        //    var fileName = new FileInfo(filePath).Name.BeforeLastOrWhole(".");
        //    var mapFilePath = PathUtils.Combine(PathSeparator.BSlash, dir, $"{fileName}.L{layer}.lid");

        //    var bytesBlockNo = match.BlockNumber.ToByteArray().Take(3).ToArray();
        //    var bytesBlockOffset = match.BlockOffset.ToByteArray().Take(2).ToArray();
        //    var bytesChunkLength = match.ChunkLength.ToByteArray().Take(1).ToArray();

        //    await FileUtils.AppendAllBytesAsync(mapFilePath, bytesBlockNo.ConcatMany(bytesBlockOffset, bytesChunkLength).ToArray());
        //    return mapFilePath;
        //}

        private static async Task<string> SaveToFileAsync(string filePath, List<RawBlockchainMatch> matches, int layer, byte[] chunk)
        {
            var dir = Path.GetDirectoryName(filePath);
            var fileName = new FileInfo(filePath).Name.BeforeLastOrWhole(".").BeforeLastOrWhole(".L");
            var mapFilePath = PathUtils.Combine(PathSeparator.BSlash, dir, $"{fileName}.L{layer}.lid");
            var flagsFilePath = PathUtils.Combine(PathSeparator.BSlash, dir, $"{fileName}.L{layer}.lid.flags");
            
            if (!matches.Select(m => m.BlockNumber).AllEqual() && matches.Any(m => m.BlockNumber > 255))
                throw new ArgumentOutOfRangeException(null, @"Invalid BLock number, it has to fit into a byte");
            if (matches.Any(m => m.ChunkLength != 2))
                throw new ArgumentOutOfRangeException(null, @"All chunks must have 2 bytes");
            if (matches.Any(m => m.BlockOffset > BitUtils.MaxNumberStoredForBits(12)))
                throw new ArgumentOutOfRangeException(null, @"Offset must accomodate for a chunk length");
            if (matches.Count != 4)
                throw new ArgumentOutOfRangeException(null, @"Each batch should have 4 bytes");

            var blockNo = matches[0].BlockNumber.ToBitArray<bool>().Take(16).ToArray();
            if (blockNo.Skip(8).All(bit => bit == false))
                blockNo = blockNo.Take(8).ToArray();

            var offsets = new bool[4][];
            for (var i = 0; i < matches.Count; i++)
                offsets[i] = matches[i].BlockOffset.ToBitArray<bool>().Take(12).ToArray();
            
            var ba = new BitArray(blockNo.ConcatMany(offsets[0], offsets[1], offsets[2], offsets[3]).ToArray());
            var encoded = ba.ToByteArray();

            await FileUtils.AppendAllBytesAsync(mapFilePath, encoded);

            await File.AppendAllTextAsync(flagsFilePath, (encoded.Length == 7 ? 1 : 0).ToString()); // if match found in first 256 block then we got compression

            return mapFilePath;
        }

        private static void RemoveCompressedFiles(string filePath)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? throw new NullReferenceException());
            var mapFiles = dir.EnumerateFiles("*.lid");
            var flagFiles = dir.EnumerateFiles("*.lid.flags");

            foreach (var file in mapFiles.Concat(flagFiles)) 
                file.Delete();
        }

        private async Task CreateExpandedBlockHashesAsync(int maxForBits)
        {
            _expandedBlockHashes.Clear();
            var maxFor16Bits = BitUtils.MaxNumberStoredForBits(16);

            for (var i = 0; i < maxFor16Bits; i++)
            {
                await OnCompressionStatusChangingAsync($"Expanding hash {i}");

                var block = await Lm.GetBlockFromDbByIndexAsync(i);
                if (block == null)
                    return;

                var blockHash = block.ExpandedBlockHash?.ToList();
                if (blockHash == null || blockHash.Count == 0)
                {
                    blockHash = block.RawData.Sha3().ToList();
                    while (blockHash.Count < maxForBits)
                        await blockHash.AddRangeAsync(blockHash.TakeLast_(32).ToArray().Sha3());
                    while (blockHash.Count > maxForBits)
                        blockHash.RemoveAt(blockHash.Count - 1);
                    await Lm.AddExpandedBlockHashToDbByIndexAsync(i, blockHash.ToArray());
                }
                
                _expandedBlockHashes.Add(blockHash.ToArray());
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
        private async Task OnCompressionStatusChangingAsync(string message) => await OnCompressionStatusChangingAsync(new CompressionStatusChangedEventArgs(0, 0, 0, message));

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

            public override string ToString() => FileOffset == 0 && FileSize == 0 && Layer == 0 
                ? $"{Message}..."
                : $"{Message} ({FileOffset.ToFileSizeString()} / {FileSize.ToFileSizeString()}: L{Layer})";
        }
    }
}
