using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BlockchainEncoder.Source.Common.Converters;
using BlockchainEncoder.Source.DbContext;
using BlockchainEncoder.Source.DbContext.Models;
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

namespace BlockchainEncoder.Source.Services
{
    public class LitecoinManager : ILitecoinManager
    {
        private bool _pauseBlockchainSync;
        private bool _isBlockchainSyncPaused = true;
        private LocalDbContext _db;
        private RPCClient _rpc;
        private NetworkCredential _prevCredentials;
        private readonly List<DbRawBlock> _rawBlocksToAdd = new();

        public NetworkCredential Credentials => ConfigUtils.GetRPCNetworkCredential("Litecoin");
        public LocalDbContext Db => _db ??= new LocalDbContext();
        public RPCClient Rpc
        {
            get
            {
                var currCredentials = Credentials;
                if (currCredentials.Equals_(_prevCredentials)) 
                    return _rpc;

                _rpc = new RPCClient(Credentials, Credentials.Domain.ToUri(), Network.Main);
                _prevCredentials = currCredentials;
                return _rpc;
            }
        }

        public async Task<RawBlock> GetNextRawBlockAsync(DbRawBlock previousBlock)
        {
            var blockIndex = previousBlock == null ? 0 : (int)(previousBlock.Index + 1);

            return (await Rpc.GetBlockAsync(await Rpc.GetBlockHashAsync(blockIndex))).ToRawBlock(blockIndex);
        }

        public async Task<RawBlock> GetLastRawBlockAsync()
        {
            var lastBlockHash = await Rpc.GetBestBlockHashAsync();
            var lastBlockHeight = (await Rpc.GetBlockStatsAsync(lastBlockHash)).Height;
            var resp = await Rpc.SendCommandAsync(RPCOperations.getblock, CancellationToken.None, lastBlockHash, false);

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
            try
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

                return nextBlock;
            }
            finally
            {
                _isBlockchainSyncPaused = true;
            }
        }

        public async Task PauseSyncingRawBlockchainAsync()
        {
            _pauseBlockchainSync = true;
            await TaskUtils.WaitUntil(() => _isBlockchainSyncPaused);
        }

        public async Task<int> GetDbBlockCountAsync() => await Db.RawBlocks.CountAsync();

        public async Task<int> GetBlockCountAsync() => (await Rpc.GetBlockStatsAsync(await Rpc.GetBestBlockHashAsync())).Height;

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

        public async Task<RawBlock> GetRawBlockByIndexAsync(int blockIndex) => await Rpc.GetBlockAsync(blockIndex).ToRawBlock(blockIndex);

        public async Task<DbRawBlock> AddRawBlockToDbAsync(DbRawBlock block)
        {
            await Db.RawBlocks.AddAsync(block);
            return block;
        }

        public async Task<DbRawBlock> AddRawBlockToDbByIndexAsync(int blockIndex)
        {
            var block = await AddRawBlockToDbAsync(await GetRawBlockByIndexAsync(blockIndex).ToDbRawBlock());
            await Db.SaveChangesAsync();
            return block;
        }

        public async Task<int> GetTwoByteMapsCountAsync() => await Db.TwoByteMaps.CountAsync();
        public async Task ClearTwoByteMapsAsync()
        {
            await Db.TwoByteMaps.ClearAsync();
            await Db.SaveChangesAsync();
        }

        public async Task<DbTwoBytesMap> AddTwoByteMapToDbAsync(DbTwoBytesMap twoByteMap)
        {
            await Db.TwoByteMaps.AddAsync(twoByteMap);
            await Db.SaveChangesAsync();
            return twoByteMap;
        }

        public async Task<DbTwoBytesMap> GetTwoByteMapByIndexAsync(int index)
        {
            var map = await Db.TwoByteMaps.SingleOrDefaultAsync(m => m.Index == index);
            if (map != null)
                await Db.Entry(map).Reference(e => e.Block).LoadAsync();
            return map;
        }

        public async Task<DbTwoBytesMap> GetTwoByteMapByValueAsync(byte[] value)
        {
            return await Db.TwoByteMaps.SingleAsync(m => m.Value == value);
        }

        public async Task<DbTwoBytesMap> GetTwoByteMapByValueAsync(long blockId, int blockOffset)
        {
            var map = await Db.TwoByteMaps.SingleOrDefaultAsync(m => m.BlockId == blockId && m.IndexInBlock == blockOffset);
            if (map != null)
                await Db.Entry(map).Reference(e => e.Block).LoadAsync();
            return map;
        }

        public async Task<DbTwoBytesMap[]> GetTwoByteMapsWithInvalidValueAsync()
        {
            return (await Db.TwoByteMaps.Include(m => m.Block).WhereAsync(m => m.Value == null)).ToArray();
        }

        public async Task<int[]> GetNonExistingTwoByteMapIndicesAsync()
        {
            return await Task.Run(() =>
            {
                var dbIndices = Db.TwoByteMaps.Select(m => m.Index).ToArray();
                var allIndices = Enumerable.Range(0, ByteUtils.MaxNumberStoredForBytes(2)).ToArray();
                var notExistingIndices = allIndices.Except(dbIndices).ToArray();
                return notExistingIndices.ToArray();
            });
        }

        public async Task<DbTwoBytesMap> SetTwoByteMapValueAsync(DbTwoBytesMap map, byte[] value)
        {
            var dbMap = await Db.TwoByteMaps.SingleAsync(m => m.Index == map.Index);
            dbMap.Value = value;
            await Db.SaveChangesAsync();
            return dbMap;
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
