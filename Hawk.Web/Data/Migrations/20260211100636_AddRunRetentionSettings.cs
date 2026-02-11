using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawk.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRunRetentionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RunRetentionDays",
                table: "Monitors",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserMonitorSettings",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RunRetentionDays = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMonitorSettings", x => x.UserId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserMonitorSettings");

            migrationBuilder.DropColumn(
                name: "RunRetentionDays",
                table: "Monitors");
        }
    }
}
