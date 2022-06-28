using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommonLib.Source.Common.Converters;
using CommonLib.Source.Common.Extensions;
using CommonLib.Source.Common.Extensions.Collections;
using CommonLib.Source.Common.Utils;
using CommonLib.Source.Common.Utils.TypeUtils;
using CommonLib.Source.Common.Utils.UtilClasses;
using CryptoApisLib.Source.Clients.RPCs._BaseRPC.Responses;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.RPC;
using WpfMyCompression.Source.Common.Converters;
using WpfMyCompression.Source.DbContext;
using WpfMyCompression.Source.DbContext.Models;

namespace WpfMyCompression.Source.Services
{
    public class LitecoinManager : ILitecoinManager
    {
        private bool _pauseBlockchainSync;
        private bool _isBlockchainSyncPaused = true;
        private LocalDbContext _db;
        private RPCClient _rpc;
        private NetworkCredential _credentials;
        private readonly List<DbRawBlock> _rawBlocksToAdd = new();

        public NetworkCredential Credentials => _credentials ??= ConfigUtils.GetRPCNetworkCredential("Litecoin");
        public LocalDbContext Db => _db ??= new LocalDbContext();
        public RPCClient Rpc => _rpc ??= new RPCClient(Credentials, Credentials.Domain.ToUri(), Network.Main);

        public async Task<RawBlock> GetNextRawBlockAsync(DbRawBlock previousBlock)
        {
            var blockIndex = previousBlock == null ? 0 : (int)(previousBlock.Index + 1);

            return (await Rpc.GetBlockAsync(await Rpc.GetBlockHashAsync(blockIndex))).ToRawBlock(blockIndex);
        }

        public async Task<RawBlock> GetLastRawBlockAsync()
        {
            //var tx = await Rpc.GetRawTransactionInfoAsync(uint256.Parse("c7604997dae9d07f18721f6c10fc2dd2ef9926d92ccb0ed6fffccb82c860e4cb"));
            //var tx2 = await Rpc.GetRawTransactionInfoAsync(uint256.Parse("3bed06209f382e20f7b4eb1724366835874769763744d3cc429182422c8e7950"));

            //var lastBlockHeight = await Rpc.GetBlockCountAsync() - 1;

            //
            //var customHash = (await Rpc.GetBlockHeaderAsync(2284382)).GetHash();
            //var block1 = await Rpc.GetBlockAsync(customHash);

            //
            //var block = await Rpc.GetBlockAsync(2284382);

            var lastBlockHash = await Rpc.GetBestBlockHashAsync();
            var lastBlockHeight = (await Rpc.GetBlockStatsAsync(lastBlockHash)).Height;
            ////var lastBlock = await Rpc.GetBlockAsync(lastBlockHash);
            //var tright = await Rpc.GetRawTransactionAsync(uint256.Parse("7b6dd224159fb7d591a34730586829b311fad158e0830ba3fe350f05d88a2965"));
            ////var twrong = await Rpc.GetRawTransactionAsync(uint256.Parse("3bed06209f382e20f7b4eb1724366835874769763744d3cc429182422c8e7950"));

            //var t = "801d0e493224859b9210af6f88791365b2d40fe45099feeff1474bbc69c5e9200060000000000ffffffff0112bab955d700000022582073a2485a7c127ffca4e0c39484a5145307ea86771f57b6a87be195a3813e3f2e0000000000".HexToByteArray().ToUTF8String();

            //var tparseright = Transaction.Parse("010000000001010000000000000000000000000000000000000000000000000000000000000000ffffffff56035edb2203002f42696e616e63652f343035fabe6d6d52e4d6e689f9adf8e0fe16aa8af45acc53166599171608265d93c54be2a6158f0100000000000000da88ffdd9a5b969b31359f49ac41f4dd0300ae2b78000000ffffffff0265ad964a000000001976a9142e9df0aa5f77ef3e46cbf24d3f6d32288d5ca09388ac0000000000000000266a24aa21a9ed436db6ccd5ca2292b16fc6e00a33cce22150e468786aa85e9105ff615ee2f45e0120000000000000000000000000000000000000000000000000000000000000000000000000", Network.Main);
            //var tparsewrong = Transaction.Parse("02000000000801d0e493224859b9210af6f88791365b2d40fe45099feeff1474bbc69c5e9200060000000000ffffffff0112bab955d700000022582073a2485a7c127ffca4e0c39484a5145307ea86771f57b6a87be195a3813e3f2e0000000000", Network.Main);
            ////
            var resp = await Rpc.SendCommandAsync(RPCOperations.getblock, CancellationToken.None, lastBlockHash, false);
            //var block = Network.Main.Consensus.ConsensusFactory.CreateBlock();
            //var s = new BitcoinStream(new MemoryStream(Encoders.Hex.DecodeData(resp.ResultString)), false) { ConsensusFactory = Network.Main.Consensus.ConsensusFactory };
            //block.ReadWrite(s);
            //block.ReadWrite(Encoders.Hex.DecodeData(resp.ResultString), Network.Main.Consensus.ConsensusFactory);

            //return block.ToRawBlock(lastBlockheight);

            //var block = Block.Parse(resp.Result.ToString(), Network.Main);
            //return block.ToRawBlock(lastBlockheight);

            return new RawBlock { Index = lastBlockHeight, RawData = resp.ResultString.HexToByteArray() };
        }

        private async Task<DbRawBlock> GetLastRawBlockFromDbOrCacheOrNullAsync()
        {
            var lastDbIndex = !Db.RawBlocks.Any() ? (long?)null : await Db.RawBlocks.MaxAsync(b => b.Index);
            var lastCachedIndex = _rawBlocksToAdd.MaxBy(b => b.Index)?.Index;
            var lastDbBlock = lastDbIndex == null ? null : await Db.RawBlocks.SingleAsync(b => b.Index == lastDbIndex);
            var lastCacheBlock = lastCachedIndex == null ? null : _rawBlocksToAdd.Single(b => b.Index == lastCachedIndex);
            return new[] { lastDbBlock, lastCacheBlock }.Where(b => b is not null).MaxBy(b => b.Index);
        } 

        public async Task<DbRawBlock> AddRawBlockAsync(DbRawBlock block)
        {
            _rawBlocksToAdd.Add(block);
            await InsertOrUpdateRawBlocksToDbAsync(100);
            return block;
        }

        private async Task InsertOrUpdateRawBlocksToDbAsync(int? every = null)
        {
            if (every == null || _rawBlocksToAdd.Count >= every)
            {
                await Db.RawBlocks.BulkMergeAsync(_rawBlocksToAdd, o =>
                {
                    o.ColumnPrimaryKeyExpression = b => b.Index;
                    o.MergeKeepIdentity = true;
                    o.InsertKeepIdentity = true;
                });
                //await Db.RawBlocks.AddRangeAsync(_rawBlocksToAdd);
                //await Db.SaveChangesAsync();
                _rawBlocksToAdd.Clear();
            }
        }

        public async Task<DbRawBlock> AddNextRawBlockAsync() => await AddRawBlockAsync(await GetNextRawBlockAsync(await GetLastRawBlockFromDbOrCacheOrNullAsync()).ToDbRawBlock());

        public async Task<DbRawBlock> SyncRawBlockchain()
        {
            if (!_isBlockchainSyncPaused)
                throw new AccessViolationException("Blockchain is already syncing");

            _pauseBlockchainSync = false;
            _isBlockchainSyncPaused = false;

            DbRawBlock nextBlock;
            var lastBlock = await GetLastRawBlockAsync().ToDbRawBlock();

            do
            {
                nextBlock = await AddNextRawBlockAsync();
                await OnRawBlockchainSyncStatusChangingAsync(nextBlock, lastBlock, "Syncing...");
            } while (nextBlock.Index < lastBlock.Index && !_pauseBlockchainSync);

            await InsertOrUpdateRawBlocksToDbAsync();
            await OnRawBlockchainSyncStatusChangingAsync(nextBlock, lastBlock, _pauseBlockchainSync ? "Paused" : "Synced");

            _isBlockchainSyncPaused = true;
            
            return nextBlock;
        }

        public async Task PauseSyncingRawBlockchainAsync()
        {
            _pauseBlockchainSync = true;
            await TaskUtils.WaitUntil(() => _isBlockchainSyncPaused);
        }

        public async Task<int> GetBlockCountAsync() => await Db.RawBlocks.CountAsync();

        public async Task<DbRawBlock> GetBlockFromDbByIndexAsync(int index) => await Db.RawBlocks.SingleOrDefaultAsync(b => b.Index == index);

        public async Task<byte[]> GetExpandedBlockHashFromDbByindexAsync(int index) => (await GetBlockFromDbByIndexAsync(index))?.ExpandedBlockHash;

        public async Task<byte[]> AddExpandedBlockHashToDbByIndexAsync(int index, byte[] blockHash)
        {
            Db.RawBlocks.Single(b => b.Index == index).ExpandedBlockHash = blockHash;
            await Db.SaveChangesAsync();
            return blockHash;
        }

        public async Task<DbRawBlock[]> GetBlocksWithInvalidExpandedHashesAsync()
        {
            var maxSize = BitUtils.MaxNumberStoredForBits(12) + 1;
            return await (await Db.RawBlocks.WhereAsync(b => b.ExpandedBlockHash == null || b.ExpandedBlockHash.Length < maxSize)).ToArrayAsync();
        }

        public event MyAsyncEventHandler<ILitecoinManager, RawBlockchainSyncStatusChangedEventArgs> RawBlockchainSyncStatusChanged;

        private async Task OnRawBlockchainSyncStatusChangingAsync(RawBlockchainSyncStatusChangedEventArgs e) => await RawBlockchainSyncStatusChanged.InvokeAsync(this, e);
        private async Task OnRawBlockchainSyncStatusChangingAsync(DbRawBlock block, DbRawBlock lastBlock, string message) => await OnRawBlockchainSyncStatusChangingAsync(new RawBlockchainSyncStatusChangedEventArgs(block, lastBlock, message));
        public async Task NotifyBlockchainSyncStatusChangedAsync() => await OnRawBlockchainSyncStatusChangingAsync(await GetLastRawBlockFromDbOrCacheOrNullAsync(), await GetLastRawBlockAsync().ToDbRawBlock(), "Sync not started");

        public class RawBlockchainSyncStatusChangedEventArgs : EventArgs
        {
            public DbRawBlock Block { get; }
            public DbRawBlock LastBlock { get; }
            public string Message { get; }

            public RawBlockchainSyncStatusChangedEventArgs(DbRawBlock block, DbRawBlock lastBlock, string message)
            {
                Block = block;
                LastBlock = lastBlock;
                Message = message;
            }

            public override string ToString() => $"{Message} ({Block?.Index ?? 0}/{LastBlock.Index})";
        }
    }
}
