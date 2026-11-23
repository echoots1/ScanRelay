using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScanRelay.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterGroupName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SyncGroupName",
                table: "Printers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncGroupName",
                table: "Printers");
        }
    }
}
