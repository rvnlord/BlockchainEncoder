namespace WpfMyCompression.Source.DbContext.Models
{
    public class DbRawBlock
    {
        public long Index { get; set; }  
        public byte[] RawData { get; set; }
        public byte[] ExpandedBlockHash { get; set; }
    }
}
