﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WpfMyCompression.Source.DbContext;

#nullable disable

namespace WpfMyCompression.Migrations
{
    [DbContext(typeof(LocalDbContext))]
    partial class LocalDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "6.0.6");

            modelBuilder.Entity("WpfMyCompression.Source.DbContext.Models.DbRawBlock", b =>
                {
                    b.Property<long>("Index")
                        .HasColumnType("INTEGER");

                    b.Property<byte[]>("RawData")
                        .HasColumnType("BLOB");

                    b.HasKey("Index");

                    b.ToTable("RawBlocks", (string)null);
                });

            modelBuilder.Entity("WpfMyCompression.Source.DbContext.Models.KV", b =>
                {
                    b.Property<string>("Key")
                        .HasColumnType("TEXT");

                    b.Property<string>("Value")
                        .HasColumnType("TEXT");

                    b.HasKey("Key");

                    b.ToTable("KVs", (string)null);
                });
#pragma warning restore 612, 618
        }
    }
}
