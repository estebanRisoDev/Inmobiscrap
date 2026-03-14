using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inmobiscrap.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyVerificationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveMisses",
                table: "Properties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastVerifiedAt",
                table: "Properties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SoldDetectedAt",
                table: "Properties",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveMisses",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "LastVerifiedAt",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "SoldDetectedAt",
                table: "Properties");
        }
    }
}
