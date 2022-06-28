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
        public int MaxLayers { get; }

        public CompressionEngine(int maxLayers = 0, int chunkSize = 8)
        {
            ChunkSize = chunkSize;
            MaxLayers = maxLayers;
        }

        public async Task CompressAsync(string filePath)
        {
            var currentMapSize = long.MaxValue;
            long previousMapSize;
            var layer = 0;
            var fileSize = new FileInfo(filePath).Length;
            var maxForBits = BitUtils.MaxNumberStoredForBits(12);

            await OnCompressionStatusChangingAsync("Removing files");
            RemoveCompressedFiles(filePath);

            await OnCompressionStatusChangingAsync("Expanding hashes");
            await CreateExpandedBlockHashesAsync(maxForBits);

            do
            {
                var mapFilePath = GetLayerFilePath(filePath, ++layer);
                await CompressLayerAsync(filePath, layer, fileSize);

                previousMapSize = currentMapSize;
                currentMapSize = new FileInfo(mapFilePath).Length;
            } 
            while (currentMapSize < previousMapSize && (MaxLayers == 0 || layer < MaxLayers));
            
            PostProcessLayerFilesAfterCompression(filePath, layer - 1);

            await OnCompressionStatusChangingAsync(fileSize, fileSize, layer - 1, "Compressed");
        }

        private async Task CompressLayerAsync(string filePath, int layer, long fileSize)
        {
            byte[] chunk;
            long offset = 0;

            do
            {
                await OnCompressionStatusChangingAsync(offset, fileSize, layer, "Compressing");
                
                var previousMapFilePath = GetLayerFilePath(filePath, layer);

                chunk = await FileUtils.ReadBytesAsync(layer == 1 ? filePath : previousMapFilePath, offset, ChunkSize);
                var matches = await FindBestMatchesAsync(chunk);

                await SaveToFileAsync(filePath, layer, matches);

                offset += matches?.Sum(m => m.ChunkLength) ?? ChunkSize;
            } 
            while (chunk.Length > 0);

            await PostProcessFlagsFileAfterCompressionAsync(filePath, layer);
            await OnCompressionStatusChangingAsync(fileSize, fileSize, layer, $"L{layer} Compressed");
        }

        private static async Task PostProcessFlagsFileAfterCompressionAsync(string filePath, int layer)
        {
            if (layer > 255)
                throw new ArgumentOutOfRangeException(null, @"Layer number has to be smaller than byte");

            var flagsFilePath = GetFlagsFilePath(filePath, layer);
            var flagsStr = await File.ReadAllTextAsync(flagsFilePath);
            var flagsBytes = flagsStr.BitArrayStringToBitArray().ToByteArray();
            await File.WriteAllBytesAsync(flagsFilePath, flagsBytes.Append_((byte)layer).ToArray());
        }

        private static void PostProcessLayerFilesAfterCompression(string originalFilePath, int layer)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(originalFilePath) ?? throw new NullReferenceException());
            var fileName = new FileInfo(originalFilePath).Name.BeforeLastOrWhole(".");
            var compressedFiles = dir.EnumerateFiles($"{fileName}.L*.lid*").ToArray();
            var fileExcludingCurrentLayer = compressedFiles.Where(f => !f.Name.Between(".L", ".").Equals(layer.ToString())).ToArray();
            var currentLayerFiles = compressedFiles.Except(fileExcludingCurrentLayer).ToArray();

            foreach (var file in fileExcludingCurrentLayer.Where(f => !f.Name.EndsWithInvariant(".flags"))) 
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
        
        private static async Task SaveToFileAsync(string filePath, int layer, List<RawBlockchainMatch> matches)
        {
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

            var mapFilePath = GetLayerFilePath(filePath, layer);
            await FileUtils.AppendAllBytesAsync(mapFilePath, encoded);

            var flagsFilePath = GetFlagsFilePath(filePath, layer);
            await File.AppendAllTextAsync(flagsFilePath, (encoded.Length == 7 ? 1 : 0).ToString()); // if match found in first 256 block then we got compression
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
            var blocksWithInvalidHashes = await Lm.GetBlocksWithInvalidExpandedHashesAsync();
           
            foreach (var block in blocksWithInvalidHashes)
            {
                await OnCompressionStatusChangingAsync($"Expanding hash {block.Index}");
                
                var blockHash = block.RawData.Sha3().ToList();
                while (blockHash.Count < maxForBits + 1)
                    await blockHash.AddRangeAsync(blockHash.TakeLast_(1).ToArray().Sha3());
                while (blockHash.Count > maxForBits + 1)
                    blockHash.RemoveLast();
                await Lm.AddExpandedBlockHashToDbByIndexAsync((int)block.Index, blockHash.ToArray());
            }

            var blockCount = await Lm.GetBlockCountAsync();
            if (_expandedBlockHashes.Count != blockCount)
            {
                _expandedBlockHashes.Clear();
                for (var i = 0; i < blockCount; i++)
                {
                    await OnCompressionStatusChangingAsync($"Caching hash {i}");
                    var blockHash = await Lm.GetExpandedBlockHashFromDbByindexAsync(i);
                    await _expandedBlockHashes.AddAsync(blockHash.ToArray());
                }
            }
        }
        
        public async Task DecompressAsync(string compressedFilePath)
        {
            var fileSize = new FileInfo(compressedFilePath).Length;
            var maxForBits = BitUtils.MaxNumberStoredForBits(12);
            var originalFilePath = compressedFilePath;

            await OnCompressionStatusChangingAsync("Expanding hashes");
            await CreateExpandedBlockHashesAsync(maxForBits);

            await OnCompressionStatusChangingAsync("Getting layers amount");
            var layer = (await GetNumberOfLayersAsync(compressedFilePath)).ToInt();
            
            while (layer > 0)
                compressedFilePath = await DecompressLayerAsync(compressedFilePath, layer--, fileSize);
            
            PostProcessLayerFilesAfterDecompression(originalFilePath, layer + 1);

            await OnCompressionStatusChangingAsync(fileSize, fileSize, layer + 1, "Deompressed");
        }

        private static async Task<byte[]> GetFlagsFileForMapFilePathAsync(string compressedFilePath)
        {
            return await FileUtils.ReadBytesAsync(
                PathUtils.Combine(PathSeparator.BSlash, compressedFilePath, ".flags"),
                new FileInfo(compressedFilePath).Length - 1, 1);
        }

        private static string GetLayerFilePath(string originalFIlePath, int layer)
        {
            var dir = Path.GetDirectoryName(originalFIlePath);
            var fileName = new FileInfo(originalFIlePath).Name.BeforeLastOrWhole(".").BeforeLastOrWhole(".L");
            return PathUtils.Combine(PathSeparator.BSlash, dir, $"{fileName}.L{layer}.lid");
        }

        private static string GetFlagsFilePath(string originalFIlePath, int layer) => GetFlagsFilePathForLayerPath(GetLayerFilePath(originalFIlePath, layer));
        private static string GetFlagsFilePathForLayerPath(string layerFilePath) => $"{layerFilePath}.flags";

        private static async Task<byte> GetNumberOfLayersAsync(string compressedFilePath) => (await GetFlagsFileForMapFilePathAsync(compressedFilePath)).Single();
        private static async Task<BitArray> GetFlagsAsync(string compressedFilePath)
            => (await GetFlagsFileForMapFilePathAsync(compressedFilePath)).SkipLast_(1).ToArray().ToBitArray();
        
        private async Task<string> DecompressLayerAsync(string compressedFilePath, int layer, long fileSize)
        {
            byte[] compressedChunk;
            long offset = 0;
            string mapFilePath;

            var layerFlags = await GetFlagsAsync(compressedFilePath);

            do
            {
                await OnCompressionStatusChangingAsync(offset, fileSize, layer, "Deompressing");

                var compressedChunkSize = layerFlags[layer] ? 7 : 8;
                compressedChunk = await FileUtils.ReadBytesAsync(compressedFilePath, offset, compressedChunkSize);
              
                var decodedBytes = await DecodeMatchesFromBlockchainAsync(compressedChunk);
                
                mapFilePath = await SaveToFileAsync(compressedFilePath, layer, decodedBytes);
                
                offset += compressedChunkSize;
            } 
            while (compressedChunk.Length > 0);

            await PostProcessFlagsFileAfterCompressionAsync(compressedFilePath, layer);
           
            await OnCompressionStatusChangingAsync(fileSize, fileSize, layer, $"L{layer} Compressed");

            // remove end byte

            return mapFilePath;
        }

        private async Task<byte[]> DecodeMatchesFromBlockchainAsync(byte[] compressedChunk)
        {
            if (!compressedChunk.Length.In(7, 8))
                throw new ArgumentOutOfRangeException(null, @"Invalid compressed chunk size");

            var chunkBits = compressedChunk.ToBitArray<bool>();
            var blockIndex = (compressedChunk.Length == 7 ? chunkBits.Take(8) : chunkBits.Take(16)).BitArrayToByteArray().ToInt();
            var offsets = compressedChunk.Skip(compressedChunk.Length == 7 ? 8 : 16).Batch(12).Select(b => b.BitArrayToByteArray().ToInt()).ToArray();

            var blockExpandedHash = await Lm.GetExpandedBlockHashFromDbByindexAsync(blockIndex) ?? throw new NullReferenceException("No expanded hash for the given block");
            var decodedBytes = new byte[4];
            for (var i = 0; i < offsets.Length; i++)
                decodedBytes[i] = blockExpandedHash[offsets[i]];

            return decodedBytes;
        }

        private static async Task<string> SaveToFileAsync(string compressedFilePath, int layer, byte[] decodedBytes)
        {
            if (decodedBytes.Length == 8)
                throw new ArgumentOutOfRangeException(null, @"Invalid decoded bytes size");

            var mapFilePath = GetLayerFilePath(compressedFilePath, layer - 1);
            await FileUtils.AppendAllBytesAsync(mapFilePath, decodedBytes);
            
            return mapFilePath;
        }

        private static void PostProcessLayerFilesAfterDecompression(string originalFilePath, int layer)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(originalFilePath) ?? throw new NullReferenceException());
            var fileName = new FileInfo(originalFilePath).Name.BeforeLastOrWhole(".");
            var compressedFiles = dir.EnumerateFiles($"{fileName}.L*.lid*").ToArray();
            var fileExcludingCurrentLayer = compressedFiles.Where(f => !f.Name.Between(".L", ".").Equals((layer - 1).ToString())).ToArray();
            var currentLayerFiles = compressedFiles.Except(fileExcludingCurrentLayer).ToArray();

            foreach (var file in fileExcludingCurrentLayer.Where(f => !f.Name.EndsWithInvariant(".flags"))) 
                file.Delete();

            foreach (var file in currentLayerFiles) 
                file.Rename($"{file.Name.BeforeLast(".L")}.lid{file.Name.AfterLastOrNull("lid")}");
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
