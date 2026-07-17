using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeriYonetim.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantSchemaName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SchemaName",
                table: "Tenants",
                type: "character varying(63)",
                maxLength: 63,
                nullable: false,
                defaultValue: "");

            // Mevcut tenant'lar için şema adını slug'dan türet (tire → alt çizgi),
            // yoksa hepsi '' kalır ve aşağıdaki unique index oluşturulamaz.
            migrationBuilder.Sql(
                """UPDATE "Tenants" SET "SchemaName" = 'tenant_' || replace("Slug", '-', '_');""");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_SchemaName",
                table: "Tenants",
                column: "SchemaName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tenants_SchemaName",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SchemaName",
                table: "Tenants");
        }
    }
}
