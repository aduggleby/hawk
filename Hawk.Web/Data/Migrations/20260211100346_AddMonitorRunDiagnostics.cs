using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawk.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitorRunDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "MonitorRuns",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestBodySnippet",
                table: "MonitorRuns",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestContentType",
                table: "MonitorRuns",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHeadersJson",
                table: "MonitorRuns",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestMethod",
                table: "MonitorRuns",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestTimeoutMs",
                table: "MonitorRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestUrl",
                table: "MonitorRuns",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ResponseContentLength",
                table: "MonitorRuns",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseContentType",
                table: "MonitorRuns",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseHeadersJson",
                table: "MonitorRuns",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Reason",
                table: "MonitorRuns");

            migrationBuilder.DropColumn(
                name: "RequestBodySnippet",
                table: "MonitorRuns");

            migrationBuilder.DropColumn(
                name: "RequestContentType",
                table: "MonitorRuns");

            migrationBuilder.DropColumn(
                name: "RequestHeadersJson",
                table: "MonitorRuns");

            migrationBuilder.DropColumn(
                name: "RequestMethod",
                table: "MonitorRuns");

            migrationBuilder.DropColumn(
                name: "RequestTimeoutMs",
                table: "MonitorRuns");

            migrationBuilder.DropColumn(
                name: "RequestUrl",
                table: "MonitorRuns");

            migrationBuilder.DropColumn(
                name: "ResponseContentLength",
                table: "MonitorRuns");

            migrationBuilder.DropColumn(
                name: "ResponseContentType",
                table: "MonitorRuns");

            migrationBuilder.DropColumn(
                name: "ResponseHeadersJson",
                table: "MonitorRuns");
        }
    }
}
