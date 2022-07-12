using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlockchainEncoder.Migrations
{
    public partial class AddRawBlocks : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RawBlocks",
                columns: table => new
                {
                    Index = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RawData = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawBlocks", x => x.Index);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RawBlocks");
        }
    }
}
