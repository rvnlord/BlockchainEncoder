using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlockchainEncoder.Migrations
{
    public partial class ExpandedBlockHash : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "ExpandedBlockHash",
                table: "RawBlocks",
                type: "BLOB",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpandedBlockHash",
                table: "RawBlocks");
        }
    }
}
