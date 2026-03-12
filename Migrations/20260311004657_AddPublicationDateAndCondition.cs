using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inmobiscrap.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicationDateAndCondition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Condition",
                table: "PropertySnapshots",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublicationDate",
                table: "PropertySnapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Condition",
                table: "Properties",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublicationDate",
                table: "Properties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Condition",
                table: "Properties",
                column: "Condition");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Properties_Condition",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "Condition",
                table: "PropertySnapshots");

            migrationBuilder.DropColumn(
                name: "PublicationDate",
                table: "PropertySnapshots");

            migrationBuilder.DropColumn(
                name: "Condition",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "PublicationDate",
                table: "Properties");
        }
    }
}
