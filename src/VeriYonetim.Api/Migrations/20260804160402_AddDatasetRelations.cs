using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeriYonetim.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDatasetRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatasetRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FromDatasetId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromColumn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ToDatasetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToColumn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatasetRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatasetRelations_Datasets_FromDatasetId",
                        column: x => x.FromDatasetId,
                        principalTable: "Datasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatasetRelations_Datasets_ToDatasetId",
                        column: x => x.ToDatasetId,
                        principalTable: "Datasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatasetRelations_FromDatasetId_FromColumn_ToDatasetId_ToCol~",
                table: "DatasetRelations",
                columns: new[] { "FromDatasetId", "FromColumn", "ToDatasetId", "ToColumn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatasetRelations_ToDatasetId",
                table: "DatasetRelations",
                column: "ToDatasetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatasetRelations");
        }
    }
}
