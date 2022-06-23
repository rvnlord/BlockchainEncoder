using System.Threading.Tasks;
using CryptoApisLib.Source.Clients.RPCs._BaseRPC.Responses;
using NBitcoin;
using WpfMyCompression.Source.DbContext.Models;

namespace WpfMyCompression.Source.Common.Converters
{
    public static class RawBlockConverter
    {
        public static DbRawBlock ToDbRawBlock(this RawBlock rawBlock) => new() { Index = rawBlock.Index, RawData = rawBlock.RawData };
        public static async Task<DbRawBlock> ToDbRawBlock(this Task<RawBlock> rawBlock) => (await rawBlock).ToDbRawBlock();
        public static RawBlock ToRawBlock(this Block block, int index) => new() { Index = index, RawData = block.ToBytes() };
        public static async Task<RawBlock> ToRawBlock(this Task<Block> block, int index) => (await block).ToRawBlock(index);
    }
}
