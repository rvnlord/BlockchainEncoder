using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WpfMyCompression.Migrations
{
    public partial class TwoByteMaps : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KVs");

            migrationBuilder.CreateTable(
                name: "TwoByteMaps",
                columns: table => new
                {
                    Index = table.Column<string>(type: "TEXT", nullable: false),
                    Block = table.Column<int>(type: "INTEGER", nullable: false),
                    IndexInBLock = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TwoByteMaps", x => x.Index);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TwoByteMaps");

            migrationBuilder.CreateTable(
                name: "KVs",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KVs", x => x.Key);
                });
        }
    }
}
