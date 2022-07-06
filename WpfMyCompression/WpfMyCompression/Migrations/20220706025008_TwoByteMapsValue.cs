using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WpfMyCompression.Migrations
{
    public partial class TwoByteMapsValue : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Value",
                table: "TwoByteMaps",
                type: "BLOB",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Value",
                table: "TwoByteMaps");
        }
    }
}
