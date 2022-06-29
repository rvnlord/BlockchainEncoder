using System.Threading.Tasks;
using CommonLib.Source.Common.Utils.UtilClasses;
using CryptoApisLib.Source.Clients.RPCs._BaseRPC.Responses;
using WpfMyCompression.Source.DbContext;
using WpfMyCompression.Source.DbContext.Models;

namespace WpfMyCompression.Source.Services
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
        public Task<RawBlock> GetRawBlockByIndexAsync(int blockIndex);
        public Task<DbRawBlock> AddRawBlockToDbAsync(DbRawBlock block);
        public Task<DbRawBlock> AddRawBlockToDbByIndexAsync(int blockIndex);
        
        event MyAsyncEventHandler<ILitecoinManager, LitecoinManager.RawBlockchainSyncStatusChangedEventArgs> RawBlockchainSyncStatusChanged;
        
    }
}
