using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeriYonetim.Api.Migrations
{
    /// <summary>
    /// İzleyicilere ardışık hata sayacı eklenir, ölü <c>Model</c> kolonu düşürülür.
    ///
    /// <c>ConsecutiveFailures</c>: tek bir başarısız koşu artık izleyiciyi kırık saymıyor.
    /// Sayıyordu ve bedeli tek bir olay için üç uyarıydı — eşik aşıldı, bir koşuda
    /// bağlantı titredi (kırık + eşik durumu sıfırlandı), sonraki koşuda değer hâlâ aynı
    /// olduğu için "yeni geçiş" sanıldı. Veride hiçbir şey değişmeden üç e-posta.
    ///
    /// <c>Model</c>: kod incelemesinde ölü olduğu görüldü. Alan "planı üreten model"i
    /// vaat ediyordu ama hiçbir yerde yazılmıyordu ve AskMessage'da da karşılığı yok, yani
    /// kolon HER SATIRDA boştu. EF'in ürettiği "veri kaybı olabilir" uyarısı bu yüzden
    /// gerçek bir kayba işaret etmiyor.
    /// </summary>
    public partial class IzleyiciHataSayaci : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Model",
                table: "DatasetWatches");

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailures",
                table: "DatasetWatches",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveFailures",
                table: "DatasetWatches");

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "DatasetWatches",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }
    }
}
