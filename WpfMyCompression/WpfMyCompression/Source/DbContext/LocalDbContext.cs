using CommonLib.Source.Common.Utils.UtilClasses;
using Microsoft.EntityFrameworkCore;
using WpfMyCompression.Source.DbContext.Models;

namespace WpfMyCompression.Source.DbContext
{
    public sealed class LocalDbContext : Microsoft.EntityFrameworkCore.DbContext
    {
        public DbSet<KV> KVs { get; set; }
        public DbSet<DbRawBlock> RawBlocks { get; set; }

        public LocalDbContext() : base(DbContextFactory.GetSQLiteDbContextOptions<LocalDbContext>()) { }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<KV>()
                .ToTable("KVs")
                .HasKey(e => e.Key);
            mb.Entity<KV>().Property(e => e.Key).ValueGeneratedNever();

            mb.Entity<DbRawBlock>()
                .ToTable("RawBlocks")
                .HasKey(e => e.Index);
            mb.Entity<DbRawBlock>().Property(e => e.Index).ValueGeneratedNever();
        }
    }
}
