using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawk.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitorPausedState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPaused",
                table: "Monitors",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPaused",
                table: "Monitors");
        }
    }
}
