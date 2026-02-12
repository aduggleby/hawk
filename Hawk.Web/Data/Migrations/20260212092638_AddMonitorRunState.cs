using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawk.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitorRunState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "MonitorRuns",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "completed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "State",
                table: "MonitorRuns");
        }
    }
}
