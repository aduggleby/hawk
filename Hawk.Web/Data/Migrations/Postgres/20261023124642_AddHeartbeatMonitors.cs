using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawk.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddHeartbeatMonitors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HeartbeatGraceSeconds",
                table: "Monitors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "HeartbeatToken",
                table: "Monitors",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPingAt",
                table: "Monitors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "Monitors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Monitors_HeartbeatToken",
                table: "Monitors",
                column: "HeartbeatToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Monitors_HeartbeatToken",
                table: "Monitors");

            migrationBuilder.DropColumn(
                name: "HeartbeatGraceSeconds",
                table: "Monitors");

            migrationBuilder.DropColumn(
                name: "HeartbeatToken",
                table: "Monitors");

            migrationBuilder.DropColumn(
                name: "LastPingAt",
                table: "Monitors");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Monitors");
        }
    }
}
