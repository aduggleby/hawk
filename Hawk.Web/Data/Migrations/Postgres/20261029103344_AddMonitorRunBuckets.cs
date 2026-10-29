using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawk.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddMonitorRunBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitorRunBuckets",
                columns: table => new
                {
                    MonitorId = table.Column<Guid>(type: "uuid", nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RunCount = table.Column<int>(type: "integer", nullable: false),
                    FailureCount = table.Column<int>(type: "integer", nullable: false),
                    TotalDurationMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitorRunBuckets", x => new { x.MonitorId, x.BucketStart });
                    table.ForeignKey(
                        name: "FK_MonitorRunBuckets_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitorRunBuckets_BucketStart",
                table: "MonitorRunBuckets",
                column: "BucketStart");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitorRunBuckets");
        }
    }
}
