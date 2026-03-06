using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Inmobiscrap.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "Properties",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeenAt",
                table: "Properties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "Properties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ListingStatus",
                table: "Properties",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true,
                defaultValue: "active");

            migrationBuilder.AddColumn<decimal>(
                name: "PreviousPrice",
                table: "Properties",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PriceChangedAt",
                table: "Properties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimesScraped",
                table: "Properties",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "PropertySnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    ScrapedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    BotId = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Bedrooms = table.Column<int>(type: "integer", nullable: true),
                    Bathrooms = table.Column<int>(type: "integer", nullable: true),
                    Area = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    PropertyType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    HasChanges = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ChangedFields = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertySnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertySnapshots_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Fingerprint",
                table: "Properties",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_ListingStatus",
                table: "Properties",
                column: "ListingStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_SourceUrl",
                table: "Properties",
                column: "SourceUrl");

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_BotId",
                table: "PropertySnapshots",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_Property_Date",
                table: "PropertySnapshots",
                columns: new[] { "PropertyId", "ScrapedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_PropertyId",
                table: "PropertySnapshots",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_ScrapedAt",
                table: "PropertySnapshots",
                column: "ScrapedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertySnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Properties_Fingerprint",
                table: "Properties");

            migrationBuilder.DropIndex(
                name: "IX_Properties_ListingStatus",
                table: "Properties");

            migrationBuilder.DropIndex(
                name: "IX_Properties_SourceUrl",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "FirstSeenAt",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "ListingStatus",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "PreviousPrice",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "PriceChangedAt",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "TimesScraped",
                table: "Properties");
        }
    }
}
