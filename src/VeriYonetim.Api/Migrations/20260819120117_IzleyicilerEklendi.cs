using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeriYonetim.Api.Migrations
{
    /// <inheritdoc />
    public partial class IzleyicilerEklendi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlanJson",
                table: "AskMessages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DatasetWatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Question = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    ConditionKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConditionOp = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Threshold = table.Column<decimal>(type: "numeric", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    LastValue = table.Column<decimal>(type: "numeric", nullable: true),
                    PreviousValue = table.Column<decimal>(type: "numeric", nullable: true),
                    IsBreaching = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastTriggeredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatasetWatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatasetWatches_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatasetWatches_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DatasetWatchRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    RanAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Value = table.Column<decimal>(type: "numeric", nullable: true),
                    Breached = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    Notified = table.Column<bool>(type: "boolean", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatasetWatchRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatasetWatchRuns_DatasetWatches_WatchId",
                        column: x => x.WatchId,
                        principalTable: "DatasetWatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatasetWatches_CreatedByUserId",
                table: "DatasetWatches",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DatasetWatches_IsEnabled_NextRunAt",
                table: "DatasetWatches",
                columns: new[] { "IsEnabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DatasetWatches_TenantId",
                table: "DatasetWatches",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DatasetWatchRuns_WatchId_RanAt",
                table: "DatasetWatchRuns",
                columns: new[] { "WatchId", "RanAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatasetWatchRuns");

            migrationBuilder.DropTable(
                name: "DatasetWatches");

            migrationBuilder.DropColumn(
                name: "PlanJson",
                table: "AskMessages");
        }
    }
}
