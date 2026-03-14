using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inmobiscrap.Migrations
{
    /// <inheritdoc />
    public partial class AddIsArriendoToProperty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsArriendo",
                table: "Properties",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArriendo",
                table: "PropertySnapshots",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsArriendo",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "IsArriendo",
                table: "PropertySnapshots");
        }
    }
}
