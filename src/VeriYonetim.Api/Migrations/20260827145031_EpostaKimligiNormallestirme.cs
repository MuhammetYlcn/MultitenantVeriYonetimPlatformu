using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeriYonetim.Api.Migrations
{
    /// <summary>
    /// Mevcut e-postaları normalleştirir (kırp + küçük harf).
    ///
    /// Şema değişikliği YOK, yalnız veri. Sebebi kod incelemesinde bulundu: giriş sayacı
    /// e-postayı küçük harfe indirgerken kayıt/davet/giriş sorguları PostgreSQL'in
    /// varsayılan harmanlamasıyla büyük-küçük harfe DUYARLI karşılaştırıyordu. İki katman
    /// aynı kimliği farklı tanımlayınca hem "ali@x.com" varken "Ali@x.com" ile ikinci bir
    /// hesap açılabiliyor, hem de kullanıcı adresini küçük harfle yazıp kendi hesabını
    /// kilitleyebiliyordu.
    ///
    /// Kod artık her yerde normalleştirilmiş kimlik kullanıyor (EmailIdentity); bu göç
    /// eski satırları o kurala uyduruyor. Böylece Users.Email üzerindeki DÜZ benzersizlik
    /// indeksi fiilen harf duyarsız bir kısıt hâline geliyor.
    /// </summary>
    public partial class EpostaKimligiNormallestirme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Küçük harfe indirgeme benzersizlik indeksini ihlal EDEBİLİR: aynı posta
            // kutusuna ait iki ayrı hesap zaten açılmış olabilir (kapatılan kusurun
            // doğrudan sonucu). Böyle bir durumda göç sessizce yarım kalmamalı — hangi
            // adreslerin çakıştığını söyleyip DURMALI, çünkü hangi hesabın tutulacağı
            // teknik değil işle ilgili bir karardır.
            migrationBuilder.Sql("""
                DO $$
                DECLARE cakisan text;
                BEGIN
                    SELECT string_agg(e, ', ') INTO cakisan
                      FROM (
                            SELECT lower(btrim("Email")) AS e
                              FROM "Users"
                             GROUP BY lower(btrim("Email"))
                            HAVING count(*) > 1
                           ) t;

                    IF cakisan IS NOT NULL THEN
                        RAISE EXCEPTION 'E-posta normallestirilemedi: su adresler yalniz harf buyukluguyle ayrisan birden fazla hesap tasiyor (%). Hangi hesabin kalacagina karar verip fazlasini silin, sonra gocu tekrar calistirin.', cakisan;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                UPDATE "Users" SET "Email" = lower(btrim("Email"))
                 WHERE "Email" <> lower(btrim("Email"))
                """);

            migrationBuilder.Sql("""
                UPDATE "PlatformAdmins" SET "Email" = lower(btrim("Email"))
                 WHERE "Email" <> lower(btrim("Email"))
                """);

            // Açık davet/sıfırlama bağlantıları da normalleştiriliyor: aksi hâlde davet
            // kabul edilirken yapılan mükerrerlik denetimi eski yazımı arar ve bulamaz.
            migrationBuilder.Sql("""
                UPDATE "AccountTokens" SET "Email" = lower(btrim("Email"))
                 WHERE "Email" <> lower(btrim("Email"))
                """);

            // Sayaç tablosu kod tarafında zaten normalleştirilmiş anahtar yazıyordu;
            // eksiksiz olsun diye buraya da konuyor (eski satır varsa temizlenir).
            migrationBuilder.Sql("""
                DELETE FROM "LoginAttempts"
                 WHERE "Email" <> lower(btrim("Email"))
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Geri alınamaz: özgün harf büyüklüğü kaydedilmiyor. Bilerek boş — sahte bir
            // geri alma yazmak, geri alınabildiği izlenimi verirdi.
        }
    }
}
