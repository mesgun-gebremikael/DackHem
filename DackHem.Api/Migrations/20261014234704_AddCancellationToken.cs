using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DackHem.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCancellationToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancellationTokenHash",
                table: "Bookings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationTokenHash",
                table: "Bookings");
        }
    }
}
