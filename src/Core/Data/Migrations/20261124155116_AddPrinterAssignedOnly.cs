using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScanRelay.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterAssignedOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AssignedOnly",
                table: "Printers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedOnly",
                table: "Printers");
        }
    }
}
