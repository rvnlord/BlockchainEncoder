using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WpfMyCompression.Migrations
{
    public partial class FKRelation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IndexInBLock",
                table: "TwoByteMaps",
                newName: "IndexInBlock");

            migrationBuilder.RenameColumn(
                name: "Block",
                table: "TwoByteMaps",
                newName: "BlockId");

            migrationBuilder.CreateIndex(
                name: "IX_TwoByteMaps_BlockId",
                table: "TwoByteMaps",
                column: "BlockId");

            migrationBuilder.AddForeignKey(
                name: "FK_TwoByteMaps_RawBlocks_BlockId",
                table: "TwoByteMaps",
                column: "BlockId",
                principalTable: "RawBlocks",
                principalColumn: "Index",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TwoByteMaps_RawBlocks_BlockId",
                table: "TwoByteMaps");

            migrationBuilder.DropIndex(
                name: "IX_TwoByteMaps_BlockId",
                table: "TwoByteMaps");

            migrationBuilder.RenameColumn(
                name: "IndexInBlock",
                table: "TwoByteMaps",
                newName: "IndexInBLock");

            migrationBuilder.RenameColumn(
                name: "BlockId",
                table: "TwoByteMaps",
                newName: "Block");
        }
    }
}
