using System;
using System.Linq;
using CommonLib.Source.Common.Utils.UtilClasses;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using WpfMyCompression.Source.DbContext.Models;

namespace WpfMyCompression.Source.DbContext
{
    public sealed class LocalDbContext : Microsoft.EntityFrameworkCore.DbContext
    {
        public DbSet<DbTwoBytesMap> TwoByteMaps { get; set; }
        public DbSet<DbRawBlock> RawBlocks { get; set; }

        public LocalDbContext() : base(DbContextFactory.GetSQLiteDbContextOptions<LocalDbContext>()) { }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<DbTwoBytesMap>()
                .ToTable("TwoByteMaps")
                .HasKey(e => e.Index);
            mb.Entity<DbTwoBytesMap>().Property(e => e.Index).ValueGeneratedNever();
            mb.Entity<DbTwoBytesMap>()
                .HasOne(e => e.Block)
                .WithMany(e => e.TwoByteMaps)
                .HasForeignKey(e => e.BlockId);
            mb.Entity<DbTwoBytesMap>().Property(e => e.Value).Metadata
                .SetValueComparer(
                    new ValueComparer<byte[]>(
                        (c1, c2) => c1.SequenceEqual(c2),
                        c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                        c => c.ToArray()));

            mb.Entity<DbRawBlock>()
                .ToTable("RawBlocks")
                .HasKey(e => e.Index);
            mb.Entity<DbRawBlock>().Property(e => e.Index).ValueGeneratedNever();
        }
    }
}
