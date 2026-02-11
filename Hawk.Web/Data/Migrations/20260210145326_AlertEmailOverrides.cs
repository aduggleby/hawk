using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawk.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertEmailOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlertEmailOverride",
                table: "Monitors",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserAlertSettings",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AlertEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAlertSettings", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserAlertSettings_AlertEmail",
                table: "UserAlertSettings",
                column: "AlertEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserAlertSettings");

            migrationBuilder.DropColumn(
                name: "AlertEmailOverride",
                table: "Monitors");
        }
    }
}
