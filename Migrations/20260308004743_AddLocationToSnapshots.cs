using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inmobiscrap.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationToSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "PropertySnapshots",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Neighborhood",
                table: "PropertySnapshots",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "PropertySnapshots",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_City",
                table: "PropertySnapshots",
                column: "City");

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_Region",
                table: "PropertySnapshots",
                column: "Region");

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_ScrapedAt_Currency",
                table: "PropertySnapshots",
                columns: new[] { "ScrapedAt", "Currency" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Snapshots_City",
                table: "PropertySnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Snapshots_Region",
                table: "PropertySnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Snapshots_ScrapedAt_Currency",
                table: "PropertySnapshots");

            migrationBuilder.DropColumn(
                name: "City",
                table: "PropertySnapshots");

            migrationBuilder.DropColumn(
                name: "Neighborhood",
                table: "PropertySnapshots");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "PropertySnapshots");
        }
    }
}
