using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeriYonetim.Api.Migrations
{
    /// <inheritdoc />
    public partial class IzleyiciTabanZamani : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastValueAt",
                table: "DatasetWatches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PreviousValueAt",
                table: "DatasetWatches",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastValueAt",
                table: "DatasetWatches");

            migrationBuilder.DropColumn(
                name: "PreviousValueAt",
                table: "DatasetWatches");
        }
    }
}
