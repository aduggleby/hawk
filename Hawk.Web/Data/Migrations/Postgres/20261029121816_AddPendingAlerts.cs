using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawk.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddPendingAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MonitorId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MonitorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Recipients = table.Column<string>(type: "text", nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Html = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    QueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SentInBatchOf = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAlerts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingAlerts_QueuedAt",
                table: "PendingAlerts",
                column: "QueuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAlerts_RunId",
                table: "PendingAlerts",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAlerts_SentAt_FailedAt",
                table: "PendingAlerts",
                columns: new[] { "SentAt", "FailedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingAlerts");
        }
    }
}
