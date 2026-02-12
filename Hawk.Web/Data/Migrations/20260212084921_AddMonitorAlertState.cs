using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawk.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitorAlertState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitorAlertStates",
                columns: table => new
                {
                    MonitorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    FailureIncidentOpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureAlertSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureAlertError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PendingRecoveryAlert = table.Column<bool>(type: "bit", nullable: false),
                    LastRecoveryAlertSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRecoveryAlertError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitorAlertStates", x => x.MonitorId);
                    table.ForeignKey(
                        name: "FK_MonitorAlertStates_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitorAlertStates_PendingRecoveryAlert",
                table: "MonitorAlertStates",
                column: "PendingRecoveryAlert");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitorAlertStates");
        }
    }
}
