using System;
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
    public class CompressionEngine : IEquatable<CompressionEngine>
    {
        private ILitecoinManager _lm;
        private readonly byte[][] _expandedBlockHashes = new byte[ByteUtils.MaxSizeStoredForBytes(2)][];
        private readonly List<bool> _filePart = new();
        private readonly bool _preloadHashesToMemory;

        public ILitecoinManager Lm => _lm ??= new LitecoinManager();
        public int ChunkSize { get; }
        public int MaxLayers { get; }
        public int MinLayers { get; }

        public CompressionEngine(int batches = 3, int minLayers = 0, int maxLayers = 0, bool preloadHashesToMemory = true)
        {
            if (maxLayers > 0 && minLayers > maxLayers)
                throw new ArgumentOutOfRangeException(nameof(minLayers));
;
            ChunkSize = batches * 2;
            MaxLayers = maxLayers;
            MinLayers = minLayers;
            _preloadHashesToMemory = preloadHashesToMemory;
        }

        public async Task<string> CompressAsync(string filePath)
        {
            var intoLayerFileSize = new FileInfo(filePath).Length;
            long fromLayerFileSize;
            var intoLayer = 0;
            var fileSize = new FileInfo(filePath).Length;

            await OnCompressionStatusChangingAsync("Removing files");
            await RemoveCompressedFilesAsync(filePath);

            if (_preloadHashesToMemory)
            {
                await OnCompressionStatusChangingAsync("Expanding hashes");
                await CreateExpandedBlockHashesAsync();
            }
            
            do
            {
                await CompressLayerAsync(filePath, ++intoLayer);

                fromLayerFileSize = intoLayerFileSize;
                intoLayerFileSize = new FileInfo(GetLayerFilePath(filePath, intoLayer)).Length;
            } 
            while (MinLayers != 0 && intoLayer < MinLayers || intoLayerFileSize < fromLayerFileSize && (MaxLayers == 0 || intoLayer <= MaxLayers));
            
            await OnCompressionStatusChangingAsync(fileSize, fileSize, intoLayer, $"Compressed into {new FileInfo(GetLayerFilePath(filePath, intoLayer)).Length.ToFileSizeString()}");

            var compressedFilePath = await PostProcessLayerFilesAfterCompressionAsync(filePath, intoLayer);
            return compressedFilePath;
        }

        private static async Task RemoveCompressedFilesAsync(string filePath)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? throw new NullReferenceException());
            var mapFiles = dir.EnumerateFiles("*.lid");

            await mapFiles.DeleteAllAsync();
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
                await blockHash.AddRangeAsync(blockHash.ToArray().Sha3());
            while (blockHash.Count > maxSize)
                blockHash.RemoveLast();
            return blockHash.ToArray();
        }

        private async Task CompressLayerAsync(string filePath, int intoLayer)
        {
            long offset = 0;
            var fromLayerFilePath = intoLayer == 1 ? filePath : GetLayerFilePath(filePath, intoLayer - 1);
            var intoLayerFilePath = GetLayerFilePath(filePath, intoLayer);
            var fromLayerFileSize = new FileInfo(fromLayerFilePath).Length;
           
            while (offset < fromLayerFileSize)
            {
                await OnCompressionStatusChangingAsync(offset, fromLayerFileSize, intoLayer, "Compressing");
                
                var chunk = await FileUtils.ReadBytesAsync(fromLayerFilePath, offset, ChunkSize);
                var matches = await FindBestMatchesAsync(chunk, offset, fromLayerFileSize, intoLayer);

                offset += ChunkSize;
                await SaveToFileAsync(filePath, intoLayer, matches, offset >= fromLayerFileSize);
            } 
            
            _filePart.Clear();
            var intoLayerFileSize = new FileInfo(intoLayerFilePath).Length;
            await OnCompressionStatusChangingAsync(fromLayerFileSize, fromLayerFileSize, intoLayer, $"L{intoLayer} Compressed into {intoLayerFileSize.ToFileSizeString()}");
        }
        
        private static async Task<string> PostProcessLayerFilesAfterCompressionAsync(string filePath, int intoLayer)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? throw new NullReferenceException());
            var fileName = new FileInfo(filePath).Name.BeforeLastOrWhole(".");
            var compressedFiles = dir.EnumerateFiles($"{fileName}.L*.lid").ToArray();
            var filesExcludingCurrentLayer = compressedFiles.Where(f => !f.Name.Between(".L", ".").Equals(intoLayer.ToString())).ToArray();
            var intoLayerFile = compressedFiles.Except(filesExcludingCurrentLayer).Single();
            
            var metadata = $"{intoLayer}|{new FileInfo(filePath).Extension.Skip(1)}".UTF8ToBase58();

            await filesExcludingCurrentLayer.DeleteAllAsync();
            await intoLayerFile.RenameAsync($"{intoLayerFile.Name.BeforeLast(".L")}-{metadata}-.lid");

            return intoLayerFile.FullName;
        }

        private async Task<List<RawBlockchainMatch>> FindBestMatchesAsync(byte[] chunk, long offset, long previousLayerFileSize, int layer)
        {
            var bestMatches = new List<RawBlockchainMatch>();
            var allBlockCount = await Lm.GetBlockCountAsync();
            var batches = chunk.Batch(2).Select(b => b.Pad(2).ToArray()).Pad(ChunkSize / 2).Select(b => b ?? new byte[2]).ToArray();

            for (var i = 0; i < allBlockCount; i++)
            {
                await OnCompressionStatusChangingAsync(offset, previousLayerFileSize, layer, $"Ensuring Block {i} validity");
                await EnsureBlockDataAvailableAsync(i);

                await OnCompressionStatusChangingAsync(offset, previousLayerFileSize, layer, $"Getting Hash for Block {i}");
                var blockHash = await GetExpandedBlockHashAsync(i);
                var matches = new List<RawBlockchainMatch>();

                for (var j = 0; j < batches.Length; j++)
                {
                    var batch = batches[j];
                    await OnCompressionStatusChangingAsync(offset, previousLayerFileSize, layer, $"Matching Batch {j}/{batches.Length}, B{i}");
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

                await OnCompressionStatusChangingAsync(offset, previousLayerFileSize, layer, $"Found {matches.Count}/{batches.Length}, B{i}");
                if (matches.Count > bestMatches.Count)
                    bestMatches.ReplaceAll(matches);
                if (matches.Count == batches.Length) // batchesNo
                    break;
            }

            Logger.For<CompressionEngine>().Info($"L{layer}: Compression, found match - B: {bestMatches[0].BlockNumber}, M: {bestMatches.Select(m => m.BlockOffset).JoinAsString(", ")} for {batches.Select(batch => batch.ToHexString().Batch(2).Select(chars => chars.JoinAsString()).JoinAsString(" ")).JoinAsString(", ")}");
            
            return bestMatches.Count >= batches.Length ? bestMatches : throw new ArgumentOutOfRangeException(null, @"Can't find 4, 2 byte batches in a single block");
        }

        private async Task EnsureBlockDataAvailableAsync(int blockIndex)
        {
            await OnCompressionStatusChangingAsync($"Checking Cached Hash, B{blockIndex}");
            await EnsureBlockHashCachedAndValidAsync(blockIndex);

            await OnCompressionStatusChangingAsync($"Checking Blocks {blockIndex}");
            await EnsureBlockInDbAsync(blockIndex);

            await OnCompressionStatusChangingAsync($"Checking Db Hash {blockIndex}");
            await EnsureExpandedBlockHashValidAsync(blockIndex);
        }

        private async Task EnsureBlockHashCachedAndValidAsync(int blockIndex)
        {
            if (blockIndex >= ByteUtils.MaxSizeStoredForBytes(2))
                return;

            var cachedHash = _expandedBlockHashes[blockIndex];
            var hashSize = BitUtils.MaxSizeStoredForBits(12);
            if (cachedHash == null || cachedHash.Length != hashSize)
            {
                await OnCompressionStatusChangingAsync($"Cached Hash Invalid, Expanding, B{blockIndex}");
                var block = await Lm.GetBlockFromDbByIndexAsync(blockIndex) ?? await Lm.AddRawBlockToDbByIndexAsync(blockIndex);
                var blockHash = block.ExpandedBlockHash != null && block.ExpandedBlockHash.Length == hashSize 
                    ? block.ExpandedBlockHash
                    : await Lm.AddExpandedBlockHashToDbByIndexAsync(blockIndex, await ExpandHashAsync(block));
                await _expandedBlockHashes.SetAsync(blockIndex, blockHash);
            }
        }

        private async Task EnsureBlockInDbAsync(int blockIndex)
        {
            if (blockIndex < ByteUtils.MaxSizeStoredForBytes(2))
                return;

            var block = await Lm.GetBlockFromDbByIndexAsync(blockIndex);
            if (block == null)
            {
                await OnCompressionStatusChangingAsync($"Block not in Db, Adding, B{blockIndex}");
                await Lm.AddRawBlockToDbByIndexAsync(blockIndex);
            }
        }

        private async Task EnsureExpandedBlockHashValidAsync(int blockIndex)
        {
            var hash = await Lm.GetExpandedBlockHashFromDbByindexAsync(blockIndex);

            if (hash == null || hash.Length != BitUtils.MaxSizeStoredForBits(12))
            {
                await OnCompressionStatusChangingAsync($"Db Hash invalid, Expanding, B{blockIndex}");
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

        private async Task SaveToFileAsync(string filePath, int intoLayer, List<RawBlockchainMatch> matches, bool isLastOffset)
        {
            if (!matches.Select(m => m.BlockNumber).AllEqual())
                throw new ArgumentOutOfRangeException(null, @"Invalid Block numbers");
            if (matches.Any(m => m.ChunkLength != 2))
                throw new ArgumentOutOfRangeException(null, @"All chunks must have 2 bytes");
            if (matches.Count != ChunkSize / 2)
                throw new ArgumentOutOfRangeException(null, @"There must be one match for every 2 bytes of data");
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
                var intoLayerFilePath = GetLayerFilePath(filePath, intoLayer);
                await FileUtils.AppendAllBytesAsync(intoLayerFilePath, encoded);
                _filePart.Clear();
            }
        }
        
        public async Task<string> DecompressAsync(string compressedFilePath)
        {
            await OnCompressionStatusChangingAsync("Removing already decompressed files");
            await RemoveLayerFilesAsync(compressedFilePath);
            
            await OnCompressionStatusChangingAsync("Getting layers amount");
            var fromLayer = GetLayerFromCompressedFile(compressedFilePath);
            var originalFromLayer = fromLayer;
            
            while (fromLayer > 0)
                await DecompressLayerAsync(compressedFilePath, fromLayer--, originalFromLayer);
            
            var fileSize = new FileInfo(compressedFilePath).Length; // 'fromLayer' is last decompressed layer less one after the loop so effectively I can use 'fromLayer' instead of creating new 'intoLayer' variable
            await OnCompressionStatusChangingAsync(fileSize, fileSize, fromLayer, $"Decompressed into {new FileInfo(GetLayerFilePath(compressedFilePath, fromLayer)).Length.ToFileSizeString()}");

            var decompressedFilePath = await PostProcessLayerFilesAfterDecompressionAsync(compressedFilePath, fromLayer);
            return decompressedFilePath;
        }

        private static async Task RemoveLayerFilesAsync(string filePath)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? throw new NullReferenceException());
            var fi = new FileInfo(filePath);
            var fileName = fi.Name.BeforeLastOrWhole(".");
            var layerFiles = dir.EnumerateFiles($"{fileName}.L*.lid");
            var decompressedFiles = dir.EnumerateFiles($"{fileName.BeforeOrWhole("-", -2)}.dec.{GetExtensionFromCompressedFile(fileName)}");
            var oldDecompressedFiles = dir.EnumerateFiles($"{fileName.BeforeOrWhole("-", -2)}.decompressed");

            await layerFiles.ConcatMany(decompressedFiles, oldDecompressedFiles).DeleteAllAsync();
        }
        
        private static string GetLayerFilePath(string compressedFIlePath, int layer)
        {
            var dir = Path.GetDirectoryName(compressedFIlePath);
            var fileName = new FileInfo(compressedFIlePath).Name.BeforeLastOrWhole(".").BeforeLastOrWhole(".L");
            return PathUtils.Combine(PathSeparator.BSlash, dir, $"{fileName}.L{layer}.lid");
        }
        
        private static string GetMetadataFromCompressedFile(string compressedFilePath) => compressedFilePath.After("-", -2).Before("-").Base58ToUTF8();
        private static int GetLayerFromCompressedFile(string compressedFilePath) => GetMetadataFromCompressedFile(compressedFilePath).Before("|").ToInt();
        private static string GetExtensionFromCompressedFile(string compressedFilePath) => GetMetadataFromCompressedFile(compressedFilePath).After("|");

        private async Task DecompressLayerAsync(string compressedFilePath, int fromLayer, int originalLayer)
        {
            long bitOffset = 0;
            long byteOFfset = 0;
            var intoLayer = fromLayer - 1;
            var maxCompressedChunkSize = (ChunkSize / 2 * 12 + 5 + 32) / 8 + 2; // +1 to complement flooring max chunk size, +1 to account for shifting byte offset to the start of the byte containing the bit offset
            var fromLayerFilePath = fromLayer == originalLayer ? compressedFilePath : GetLayerFilePath(compressedFilePath, fromLayer);
            var fromLayerFileSize = new FileInfo(fromLayerFilePath).Length;
            var intoLayerFilePath = GetLayerFilePath(compressedFilePath, intoLayer);

            while (byteOFfset < fromLayerFileSize - 2) 
            {
                await OnCompressionStatusChangingAsync(byteOFfset, fromLayerFileSize, fromLayer, "Decompressing");
                
                var compressedChunk = await FileUtils.ReadBytesAsync(fromLayerFilePath, byteOFfset, maxCompressedChunkSize);
                var varIntShift = (int)(bitOffset - byteOFfset * 8);
                var nextShiftInBits = compressedChunk.GetFirstVarIntLength(varIntShift) + ChunkSize / 2 * 12;
                var decodedBytes = await DecodeMatchesFromBlockchainAsync(compressedChunk, fromLayerFileSize, bitOffset, fromLayer, nextShiftInBits);
                
                await SaveToFileAsync(compressedFilePath, fromLayer, decodedBytes);
                
                bitOffset += nextShiftInBits;
                byteOFfset = bitOffset / 8;
            }
            
            var intoLayerFileSize = new FileInfo(intoLayerFilePath).Length;
            await OnCompressionStatusChangingAsync(fromLayerFileSize, fromLayerFileSize, fromLayer, $"L{fromLayer} Decompressed into {intoLayerFileSize.ToFileSizeString()}");

            _filePart.Clear();
        }
                                                                         
        private async Task<byte[]> DecodeMatchesFromBlockchainAsync(byte[] compressedChunk, long fromLayerFileSize, long bitOffset, int fromLayer, long nextShiftInBits)
        {
            if (compressedChunk == null || compressedChunk.Length == 0)
                throw new ArgumentNullException(nameof(compressedChunk));
            
            var byteOFfset = bitOffset / 8;
            var shift = (int)(bitOffset - byteOFfset * 8);

            var blockIndex = compressedChunk.GetFirstVarInt(shift);
            await EnsureBlockDataAvailableAsync(blockIndex);

            var varIntBlockIndexLength = compressedChunk.GetFirstVarIntLength(shift); // 5 + max 32 bits
            var blockOffsets = compressedChunk.ToBitArray<bool>().Skip(shift + varIntBlockIndexLength).Batch(12).Take(ChunkSize / 2).Select(bits => bits.ToInt()).ToArray();
            
            var blockExpandedHash = _expandedBlockHashes[blockIndex] ?? await Lm.GetExpandedBlockHashFromDbByindexAsync(blockIndex);

            var decodedBytes = new List<byte>();
            foreach (var blockOffset in blockOffsets)
                decodedBytes.AddRange(blockExpandedHash.Skip(blockOffset).Take(2));

            Logger.For<CompressionEngine>().Info($"L{fromLayer}: Decompression, decoded match - B: {blockIndex}, M: {blockOffsets.JoinAsString(", ")} into {decodedBytes.Batch(2).Select(batch => batch.ToHexString().Batch(2).Select(chars => chars.JoinAsString()).JoinAsString(" ")).JoinAsString(", ")}");
            
            return byteOFfset + nextShiftInBits / 8 >= fromLayerFileSize - 2
                ? decodedBytes.SkipLastWhile(b => b == 0).ToArray() // skip zero bytes at the end
                : decodedBytes.ToArray();
        }

        private static async Task SaveToFileAsync(string compressedFilePath, int intoLayer, byte[] decodedBytes)
        {
            var mapFilePath = GetLayerFilePath(compressedFilePath, intoLayer - 1);
            await FileUtils.AppendAllBytesAsync(mapFilePath, decodedBytes);
        }

        private static async Task<string> PostProcessLayerFilesAfterDecompressionAsync(string compressedFilePath, int intoLayer)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(compressedFilePath) ?? throw new NullReferenceException());
            var fileName = new FileInfo(compressedFilePath).Name.BeforeLastOrWhole(".");
            var compressedFiles = dir.EnumerateFiles($"{fileName}.L*.lid").ToArray();
            var fileExcludingCurrentLayer = compressedFiles.Where(f => !f.Name.Between(".L", ".").Equals(intoLayer.ToString())).ToArray();
            var intoLayerFile = compressedFiles.Except(fileExcludingCurrentLayer).Single();

            await fileExcludingCurrentLayer.DeleteAllAsync();
            await intoLayerFile.RenameAsync($"{intoLayerFile.Name.BeforeLast(".L").BeforeOrWhole("-", -2)}.dec.{GetExtensionFromCompressedFile(compressedFilePath)}");

            return intoLayerFile.FullName;
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

        public override bool Equals(object o) => Equals(o as CompressionEngine);

        public bool Equals(CompressionEngine y)
        {
            if (y is null) return false;
            if (GetType() != y.GetType()) return false;
            return _preloadHashesToMemory == y._preloadHashesToMemory && ChunkSize == y.ChunkSize && MaxLayers == y.MaxLayers && MinLayers == y.MinLayers;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(_preloadHashesToMemory, ChunkSize, MaxLayers, MinLayers);
        }

        public static bool operator ==(CompressionEngine left, CompressionEngine right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(CompressionEngine left, CompressionEngine right)
        {
            return !Equals(left, right);
        }
    }
}
