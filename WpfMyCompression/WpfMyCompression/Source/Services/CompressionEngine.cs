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
using CommonLib.Source.Common.Utils.TypeUtils;
using CommonLib.Source.Common.Utils.UtilClasses;
using MoreLinq;
using WpfMyCompression.Source.DbContext.Models;

namespace WpfMyCompression.Source.Services
{
    public class CompressionEngine
    {
        private ILitecoinManager _lm;
        private readonly byte[][] _expandedBlockHashes = new byte[ByteUtils.MaxSizeNumberForBytes(2)][];
        private readonly List<bool> _filePart = new();
        private readonly bool _preloadHashesToMemory;

        public ILitecoinManager Lm => _lm ??= new LitecoinManager();
        public int ChunkSize { get; }
        public int MaxLayers { get; }

        public CompressionEngine(int maxLayers = 0, bool preloadHashesToMemory = true)
        {
            ChunkSize = 4;
            MaxLayers = maxLayers;
            _preloadHashesToMemory = preloadHashesToMemory;
        }

        public async Task CompressAsync(string filePath)
        {
            var currentMapSize = long.MaxValue;
            long previousMapSize;
            var layer = 0;
            var fileSize = new FileInfo(filePath).Length;

            await OnCompressionStatusChangingAsync("Removing files");
            RemoveCompressedFiles(filePath);

            if (_preloadHashesToMemory)
            {
                await OnCompressionStatusChangingAsync("Expanding hashes");
                await CreateExpandedBlockHashesAsync();
            }
            
            do
            {
                var mapFilePath = GetLayerFilePath(filePath, ++layer);
                await CompressLayerAsync(filePath, layer, fileSize);

                previousMapSize = currentMapSize;
                currentMapSize = new FileInfo(mapFilePath).Length;
            } 
            while (currentMapSize < previousMapSize && (MaxLayers == 0 || layer < MaxLayers));
            
            PostProcessLayerFilesAfterCompression(filePath, layer);

            await OnCompressionStatusChangingAsync(fileSize, fileSize, layer, $"Compressed into {new FileInfo(GetLayerFilePath(filePath, layer)).Length.ToFileSizeString()}");
        }

        private static void RemoveCompressedFiles(string filePath)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? throw new NullReferenceException());
            var mapFiles = dir.EnumerateFiles("*.lid");

            foreach (var file in mapFiles) 
                file.Delete();
        }

        private async Task CreateExpandedBlockHashesAsync()
        {
            var blocksWithInvalidHashes = await Lm.GetBlocksWithInvalidExpandedHashesAsync();
           
            foreach (var block in blocksWithInvalidHashes)
            {
                await OnCompressionStatusChangingAsync($"Expanding hash {block.Index}");

                var blockHash = await ExpandHashAsync(block);
                await Lm.AddExpandedBlockHashToDbByIndexAsync((int)block.Index, blockHash);
            }
            
            var cachedInvalidHashes = await _expandedBlockHashes.WhereAsync(h => h?.Length != ByteUtils.MaxSizeStoredForBytes(2));
            var cachedInvalidHashIndices = await cachedInvalidHashes.IndexOfEachAsync(h => h is null);
            var dbBlockCount = await Lm.GetDbBlockCountAsync();

            foreach (var hashIndex in cachedInvalidHashIndices)
            {
                if (hashIndex >= dbBlockCount)
                    break;

                await OnCompressionStatusChangingAsync($"Caching hash {hashIndex}");
                var blockHash = await Lm.GetExpandedBlockHashFromDbByindexAsync(hashIndex);
                await _expandedBlockHashes.SetAsync(hashIndex, blockHash);
            }
        }

        private static async Task<byte[]> ExpandHashAsync(DbRawBlock block)
        {
            var blockHash = block.RawData.Sha3().ToList();
            var maxSize = BitUtils.MaxSizeStoredForBits(12);
            while (blockHash.Count < maxSize)
                await blockHash.AddRangeAsync(blockHash.TakeLast_(1).ToArray().Sha3());
            while (blockHash.Count > maxSize)
                blockHash.RemoveLast();
            return blockHash.ToArray();
        }

        private async Task CompressLayerAsync(string filePath, int layer, long fileSize)
        {
            long offset = 0;

            while (offset < fileSize)
            {
                await OnCompressionStatusChangingAsync(offset, fileSize, layer, "Compressing");
                
                var previousMapFilePath = layer == 1 ? filePath : GetLayerFilePath(filePath, layer);

                var chunk = await FileUtils.ReadBytesAsync(previousMapFilePath, offset, ChunkSize);
                var matches = await FindBestMatchesAsync(chunk, offset, fileSize, layer);

                offset += ChunkSize;
                await SaveToFileAsync(filePath, layer, matches, offset >= fileSize);
            } 
            
            _filePart.Clear();
            await OnCompressionStatusChangingAsync(fileSize, fileSize, layer, $"L{layer} Compressed into {new FileInfo(GetLayerFilePath(filePath, layer)).Length.ToFileSizeString()}");
        }
        
        private static void PostProcessLayerFilesAfterCompression(string originalFilePath, int layer)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(originalFilePath) ?? throw new NullReferenceException());
            var fileName = new FileInfo(originalFilePath).Name.BeforeLastOrWhole(".");
            var compressedFiles = dir.EnumerateFiles($"{fileName}.L*.lid").ToArray();
            var fileExcludingCurrentLayer = compressedFiles.Where(f => !f.Name.Between(".L", ".").Equals(layer.ToString())).ToArray();
            var currentLayerFiles = compressedFiles.Except(fileExcludingCurrentLayer).ToArray();

            foreach (var file in fileExcludingCurrentLayer) 
                file.Delete();

            foreach (var file in currentLayerFiles) 
                file.Rename($"{file.Name.BeforeLast(".L")}.lid{file.Name.AfterLastOrNull("lid")}");
        }

        private async Task<List<RawBlockchainMatch>> FindBestMatchesAsync(byte[] chunk, long offset, long fileSize, int layer)
        {
            var bestMatches = new List<RawBlockchainMatch>();
            var allBlockCount = await Lm.GetBlockCountAsync();
            var batches = chunk.Batch(2).Select(b => b.Pad(2).ToArray()).ToArray();

            for (var i = 0; i < allBlockCount; i++)
            {
                await OnCompressionStatusChangingAsync(offset, fileSize, layer, $"Ensuring Block {i} validity");
                await EnsureBlockDataAvailableAsync(i, offset, fileSize, layer);

                await OnCompressionStatusChangingAsync(offset, fileSize, layer, $"Getting Hash for Block {i}");
                var blockHash = await GetExpandedBlockHashAsync(i);
                var matches = new List<RawBlockchainMatch>();

                for (var j = 0; j < batches.Length; j++)
                {
                    var batch = batches[j];
                    await OnCompressionStatusChangingAsync(offset, fileSize, layer, $"Matching Batch {j}/{batches.Length}, B{i}");
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

                await OnCompressionStatusChangingAsync(offset, fileSize, layer, $"Found {matches.Count}/{batches.Length}, B{i}");
                if (matches.Count > bestMatches.Count)
                    bestMatches.ReplaceAll(matches);
                if (matches.Count == batches.Length) // batchesNo
                    break;
            }

            return bestMatches.Count >= batches.Length ? bestMatches : throw new ArgumentOutOfRangeException(null, @"Can't find 4, 2 byte batches in a single block");
        }

        private async Task EnsureBlockDataAvailableAsync(int blockIndex, long offset, long fileSize, int layer)
        {
            await OnCompressionStatusChangingAsync(offset, fileSize, layer, $"Checking Cached Hash, B{blockIndex}");
            await EnsureBlockHashCachedAndValidAsync(blockIndex, offset, fileSize, layer);

            await OnCompressionStatusChangingAsync(offset, fileSize, layer, $"Checking Blocks {blockIndex}");
            await EnsureBlockInDbAsync(blockIndex, offset, fileSize, layer);

            await OnCompressionStatusChangingAsync(offset, fileSize, layer, $"Checking Db Hash {blockIndex}");
            await EnsureExpandedBlockHashValidAsync(blockIndex, offset, fileSize, layer);
        }

        private async Task EnsureBlockHashCachedAndValidAsync(int blockIndex, long offset, long fileSize, int layer)
        {
            if (blockIndex >= ByteUtils.MaxSizeStoredForBytes(2))
                return;

            var cachedHash = _expandedBlockHashes[blockIndex];
            if (cachedHash == null || cachedHash.Length != BitUtils.MaxSizeStoredForBits(12))
            {
                await OnCompressionStatusChangingAsync(offset, fileSize, layer, $"Cached Hash Invalid, Expanding, B{blockIndex}");
                var block = await Lm.GetBlockFromDbByIndexAsync(blockIndex) ?? await Lm.AddRawBlockToDbByIndexAsync(blockIndex);
                var blockHash = await Lm.AddExpandedBlockHashToDbByIndexAsync(blockIndex, await ExpandHashAsync(block));
                await _expandedBlockHashes.SetAsync(blockIndex, blockHash);
            }
        }

        private async Task EnsureBlockInDbAsync(int blockIndex, long offset, long fileSize, int layer)
        {
            if (blockIndex < ByteUtils.MaxSizeStoredForBytes(2))
                return;

            var block = await Lm.GetBlockFromDbByIndexAsync(blockIndex);
            if (block == null)
            {
                await OnCompressionStatusChangingAsync(offset, fileSize, layer, $"Block not in Db, Adding, B{blockIndex}");
                await Lm.AddRawBlockToDbByIndexAsync(blockIndex);
            }
        }

        private async Task EnsureExpandedBlockHashValidAsync(int blockIndex, long offset, long fileSize, int layer)
        {
            var hash = await Lm.GetExpandedBlockHashFromDbByindexAsync(blockIndex);

            if (hash == null || hash.Length != BitUtils.MaxSizeStoredForBits(12))
            {
                await OnCompressionStatusChangingAsync(offset, fileSize, layer, $"Db Hash invalid, Expanding, B{blockIndex}");
                var blockHash = await ExpandHashAsync(await Lm.GetBlockFromDbByIndexAsync(blockIndex));
                await Lm.AddExpandedBlockHashToDbByIndexAsync(blockIndex, blockHash);
            }
        }

        private async Task<byte[]> GetExpandedBlockHashAsync(int blockIndex)
        {
            if (blockIndex < _expandedBlockHashes.Length)
                return _expandedBlockHashes[blockIndex];
            return await Lm.GetExpandedBlockHashFromDbByindexAsync(blockIndex);
        }

        private async Task SaveToFileAsync(string filePath, int layer, List<RawBlockchainMatch> matches, bool isLastOffset)
        {
            if (!matches.Select(m => m.BlockNumber).AllEqual())
                throw new ArgumentOutOfRangeException(null, @"Invalid Block numbers");
            if (matches.Any(m => m.ChunkLength != 2))
                throw new ArgumentOutOfRangeException(null, @"All chunks must have 2 bytes");
            if (matches.Any(m => m.BlockOffset > BitUtils.MaxNumberStoredForBits(12) - 1))
                throw new ArgumentOutOfRangeException(null, @"Offset must accomodate for a chunk length");

            var blockNoVarInt = matches[0].BlockNumber.ToVarInt();
            var bits = new List<bool>(blockNoVarInt);
            foreach (var m in matches)
                bits.AddRange(m.BlockOffset.ToBitArray<bool>().Take(12).ToArray());
            
            _filePart.AddRange(bits);

            if (_filePart.Count % 8 == 0 || isLastOffset)
            {
                var encoded = _filePart.BitArrayToByteArray();
                var mapFilePath = GetLayerFilePath(filePath, layer);
                await FileUtils.AppendAllBytesAsync(mapFilePath, encoded);
                _filePart.Clear();

                if (isLastOffset)
                    await FileUtils.AppendAllBytesAsync(mapFilePath, layer.ToByteArray().Take(1).ToArray());
            }
        }
        
        public async Task DecompressAsync(string compressedFilePath)
        {
            var fileSize = new FileInfo(compressedFilePath).Length;

            //await OnCompressionStatusChangingAsync("Expanding hashes");
            //await CreateExpandedBlockHashesAsync();

            await OnCompressionStatusChangingAsync("Getting layers amount");
            var layer = await GetLayerFromFile(compressedFilePath);
            
            while (layer > 0)
                await DecompressLayerAsync(compressedFilePath, layer--, fileSize);
            
            PostProcessLayerFilesAfterDecompression(compressedFilePath, layer + 1);

            await OnCompressionStatusChangingAsync(fileSize, fileSize, layer + 1, "Deompressed");
        }
        
        private static string GetLayerFilePath(string originalFIlePath, int layer)
        {
            var dir = Path.GetDirectoryName(originalFIlePath);
            var fileName = new FileInfo(originalFIlePath).Name.BeforeLastOrWhole(".").BeforeLastOrWhole(".L");
            return PathUtils.Combine(PathSeparator.BSlash, dir, $"{fileName}.L{layer}.lid");
        }
        
        private static async Task<byte> GetLayerFromFile(string compressedFilePath) => (await FileUtils.ReadBytesAsync(compressedFilePath, new FileInfo(compressedFilePath).Length - 1, 1)).Single();

        private async Task DecompressLayerAsync(string compressedFilePath, int layer, long fileSize)
        {
            long bitOffset = 0;

            while (bitOffset / 8 < fileSize - 1) // last byte is layer index
            {
                await OnCompressionStatusChangingAsync(bitOffset / 8, fileSize, layer, "Decompressing");
                
                var maxCompressedChunkSize = (int)((ChunkSize / 2 * 12 + 5 + 32) / 8 + 1 + bitOffset % 8);
                var compressedChunk = await FileUtils.ReadBytesAsync(compressedFilePath, bitOffset / 8, maxCompressedChunkSize);
                var blockIndex = compressedChunk.GetFirstVarInt();
                var varIntBlockIndexLength = compressedChunk.GetFirstVarIntLength();
                var offsets = compressedChunk.ToBitArray<bool>().Skip(varIntBlockIndexLength).Batch(12).Take(ChunkSize / 2).Select(bits => bits.ToInt()).ToArray();
              
                var decodedBytes = await DecodeMatchesFromBlockchainAsync(blockIndex, offsets, bitOffset / 8, fileSize, layer); // TODO: test from here
                
                await SaveToFileAsync(compressedFilePath, layer, decodedBytes);
                
                bitOffset += varIntBlockIndexLength + ChunkSize / 2 * 12;
            }
            
            await OnCompressionStatusChangingAsync(fileSize, fileSize, layer, $"L{layer} Decompressed");

            _filePart.Clear();
        }

        private async Task<byte[]> DecodeMatchesFromBlockchainAsync(int blockIndex, int[] blockOffsets, long offset, long fileSize, int layer)
        {
            await EnsureBlockDataAvailableAsync(blockIndex, offset, fileSize, layer);
            var blockExpandedHash = await Lm.GetExpandedBlockHashFromDbByindexAsync(blockIndex);

            var decodedBytes = new List<byte>();
            foreach (var blockOffset in blockOffsets)
                decodedBytes.AddRange(blockExpandedHash.Skip(blockOffset).Take(2));

            return decodedBytes.ToArray();
        }

        private static async Task SaveToFileAsync(string compressedFilePath, int layer, byte[] decodedBytes)
        {
            var mapFilePath = GetLayerFilePath(compressedFilePath, layer - 1);
            await FileUtils.AppendAllBytesAsync(mapFilePath, decodedBytes);
        }

        private static void PostProcessLayerFilesAfterDecompression(string originalFilePath, int layer)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(originalFilePath) ?? throw new NullReferenceException());
            var fileName = new FileInfo(originalFilePath).Name.BeforeLastOrWhole(".");
            var compressedFiles = dir.EnumerateFiles($"{fileName}.L*.lid").ToArray();
            var fileExcludingCurrentLayer = compressedFiles.Where(f => !f.Name.Between(".L", ".").Equals((layer - 1).ToString())).ToArray();
            var currentLayerFiles = compressedFiles.Except(fileExcludingCurrentLayer).ToArray();

            foreach (var file in fileExcludingCurrentLayer) 
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
