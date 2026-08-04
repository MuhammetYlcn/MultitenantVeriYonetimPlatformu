using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeriYonetim.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRelationAutoDetectedFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAutoDetected",
                table: "DatasetRelations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAutoDetected",
                table: "DatasetRelations");
        }
    }
}
