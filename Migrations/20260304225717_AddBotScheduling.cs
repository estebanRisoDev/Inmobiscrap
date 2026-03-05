using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inmobiscrap.Migrations
{
    /// <inheritdoc />
    public partial class AddBotScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CronExpression",
                table: "Bots",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ScheduleEnabled",
                table: "Bots",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CronExpression",
                table: "Bots");

            migrationBuilder.DropColumn(
                name: "ScheduleEnabled",
                table: "Bots");
        }
    }
}
