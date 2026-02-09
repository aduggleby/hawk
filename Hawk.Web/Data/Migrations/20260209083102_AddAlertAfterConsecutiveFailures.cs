// <file>
// <summary>
// EF Core migration adding alert threshold (consecutive failures) configuration to monitors.
// </summary>
// </file>

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawk.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertAfterConsecutiveFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AlertAfterConsecutiveFailures",
                table: "Monitors",
                type: "int",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlertAfterConsecutiveFailures",
                table: "Monitors");
        }
    }
}
