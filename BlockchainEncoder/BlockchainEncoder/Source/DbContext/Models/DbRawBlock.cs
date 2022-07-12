using System.Collections.Generic;

namespace BlockchainEncoder.Source.DbContext.Models
{
    public class DbRawBlock
    {
        public long Index { get; set; }  
        public byte[] RawData { get; set; }
        public byte[] ExpandedBlockHash { get; set; }

        public List<DbTwoBytesMap> TwoByteMaps { get; set; }
    }
}
