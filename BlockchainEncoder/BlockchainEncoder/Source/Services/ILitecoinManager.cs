using System.Threading.Tasks;
using BlockchainEncoder.Source.DbContext;
using BlockchainEncoder.Source.DbContext.Models;
using CommonLib.Source.Common.Utils.UtilClasses;
using CryptoApisLib.Source.Clients.RPCs._BaseRPC.Responses;

namespace BlockchainEncoder.Source.Services
{
    public interface ILitecoinManager
    {
        LocalDbContext Db { get; }

        Task<RawBlock> GetNextRawBlockAsync(DbRawBlock previousBlock);
        Task<RawBlock> GetLastRawBlockAsync();
        Task<DbRawBlock> AddRawBlockAsync(DbRawBlock block);
        Task<DbRawBlock> AddNextRawBlockAsync();
        Task<DbRawBlock> SyncRawBlockchain();
        Task PauseSyncingRawBlockchainAsync();
        Task NotifyBlockchainSyncStatusChangedAsync();
        Task<int> GetDbBlockCountAsync();
        Task<int> GetBlockCountAsync();
        Task<DbRawBlock> GetBlockFromDbByIndexAsync(int index);
        Task<byte[]> GetExpandedBlockHashFromDbByindexAsync(int index);
        Task<byte[]> AddExpandedBlockHashToDbByIndexAsync(int index, byte[] blockHash);
        Task<DbRawBlock[]> GetBlocksWithInvalidExpandedHashesAsync();
        Task<RawBlock> GetRawBlockByIndexAsync(int blockIndex);
        Task<DbRawBlock> AddRawBlockToDbAsync(DbRawBlock block);
        Task<DbRawBlock> AddRawBlockToDbByIndexAsync(int blockIndex);
        Task<int> GetTwoByteMapsCountAsync();
        Task ClearTwoByteMapsAsync();
        Task<DbTwoBytesMap> AddTwoByteMapToDbAsync(DbTwoBytesMap twoBytesMap);
        Task<DbTwoBytesMap> GetTwoByteMapByIndexAsync(int index);
        Task<DbTwoBytesMap> GetTwoByteMapByValueAsync(byte[] value);
        Task<DbTwoBytesMap[]> GetTwoByteMapsWithInvalidValueAsync();
        Task<int[]> GetNonExistingTwoByteMapIndicesAsync();
        Task<DbTwoBytesMap> SetTwoByteMapValueAsync(DbTwoBytesMap map, byte[] value);

        event MyAsyncEventHandler<ILitecoinManager, LitecoinManager.RawBlockchainSyncStatusChangedEventArgs> RawBlockchainSyncStatusChanged;
    }
}
