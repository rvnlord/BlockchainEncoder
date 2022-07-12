namespace BlockchainEncoder.Source.DbContext.Models
{
    public class DbTwoBytesMap
    {
        public int Index { get; set; }
        public long BlockId { get; set; }
        public int IndexInBlock { get; set; }
        public byte[] Value { get; set; }

        public DbRawBlock Block { get; set; }
    }
}
