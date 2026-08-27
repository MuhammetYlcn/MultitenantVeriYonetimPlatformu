using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Göreli dönem etiketlerinin ("gecenAy") mutlak aralığa çevrimini doğrular.
//
// Burası bilinçli olarak yoğun test edilir: tarih aritmetiği hatası SESSİZDİR — sorgu
// çalışır, grafik çizilir, sayı yanlıştır. Sabit bir "bugün" ile çalışıldığından testler
// takvime göre kaymaz.
public class RelativePeriodTests
{
    // 4 Ağustos 2026, SALI. Hafta ortası bir gün seçildi ki hafta başı/sonu hesabı
    // rastlantıyla doğru çıkmasın.
    private static readonly DateTime Now = new(2026, 8, 4, 14, 37, 12);

    private static (DateTime Start, DateTime End) Resolve(string token)
    {
        Assert.True(RelativePeriod.TryResolve(token, Now, out var start, out var end),
            $"'{token}' çözümlenemedi.");
        return (start, end);
    }

    [Theory]
    // gün
    [InlineData("bugun", "2026-08-04", "2026-08-05")]
    [InlineData("dun", "2026-08-03", "2026-08-04")]
    // hafta — pazartesi başlar (Türkçe takvim); 4 Ağustos salı → hafta başı 3 Ağustos
    [InlineData("buHafta", "2026-08-03", "2026-08-10")]
    [InlineData("gecenHafta", "2026-07-27", "2026-08-03")]
    // kayan pencereler — bugünü DE kapsar
    [InlineData("son7Gun", "2026-07-29", "2026-08-05")]
    [InlineData("son30Gun", "2026-07-06", "2026-08-05")]
    [InlineData("son90Gun", "2026-05-07", "2026-08-05")]
    // ay
    [InlineData("buAy", "2026-08-01", "2026-09-01")]
    [InlineData("gecenAy", "2026-07-01", "2026-08-01")]
    [InlineData("son12Ay", "2025-09-01", "2026-09-01")]
    // çeyrek — Ağustos 3. çeyrektedir (Tem-Ağu-Eyl)
    [InlineData("buCeyrek", "2026-07-01", "2026-10-01")]
    [InlineData("gecenCeyrek", "2026-04-01", "2026-07-01")]
    // yıl
    [InlineData("buYil", "2026-01-01", "2027-01-01")]
    [InlineData("gecenYil", "2025-01-01", "2026-01-01")]
    public void TryResolve_KnownToken_ReturnsExpectedHalfOpenRange(
        string token, string expectedStart, string expectedEnd)
    {
        var (start, end) = Resolve(token);

        Assert.Equal(DateTime.Parse(expectedStart), start);
        Assert.Equal(DateTime.Parse(expectedEnd), end);
    }

    [Fact]
    public void TryResolve_StripsTimeComponent()
    {
        // Now'ın saati 14:37 olmasına rağmen aralık gün sınırından başlamalı; aksi halde
        // "bugün" sorgusu sabahki kayıtları atlardı.
        var (start, _) = Resolve("bugun");
        Assert.Equal(TimeSpan.Zero, start.TimeOfDay);
    }

    [Fact]
    public void TryResolve_ReturnsUnspecifiedKind_ForTimestampWithoutTimeZone()
    {
        // Satırlardaki tarihler "timestamp without time zone" olarak yazılıyor; Npgsql
        // Kind=Utc taşıyan bir değeri o kolona yazmayı REDDEDER. Bu test o kırılmayı
        // çalışma zamanında değil burada yakalar.
        var (start, end) = Resolve("buAy");

        Assert.Equal(DateTimeKind.Unspecified, start.Kind);
        Assert.Equal(DateTimeKind.Unspecified, end.Kind);
    }

    [Fact]
    public void TryResolve_RangeIsHalfOpen_EndIsExclusive()
    {
        // "gecenAy" bitişi = "buAy" başlangıcı. Aynı ana denk gelmeleri aralıkların ne
        // boşluk ne örtüşme bırakmadığını gösterir.
        var (_, lastMonthEnd) = Resolve("gecenAy");
        var (thisMonthStart, _) = Resolve("buAy");

        Assert.Equal(thisMonthStart, lastMonthEnd);
    }

    [Fact]
    public void TryResolve_YearBoundary_UsesPreviousYear()
    {
        // Ocak ayındayken "gecenAy" bir önceki YILIN aralığına düşmeli.
        var newYear = new DateTime(2026, 1, 15);
        Assert.True(RelativePeriod.TryResolve("gecenAy", newYear, out var start, out var end));

        Assert.Equal(new DateTime(2025, 12, 1), start);
        Assert.Equal(new DateTime(2026, 1, 1), end);
    }

    [Theory]
    [InlineData("gelecekAy")]   // desteklenmeyen ama makul görünen etiket
    [InlineData("")]
    [InlineData(null)]
    public void TryResolve_UnknownToken_ReturnsFalse(string? token)
    {
        Assert.False(RelativePeriod.TryResolve(token, Now, out _, out _));
    }

    [Fact(DisplayName = "Dönem sınırları İŞ saat dilimine göre, sunucunun yerel saatine göre DEĞİL")]
    public void Now_UsesBusinessZone_NotServerLocalTime()
    {
        // Kod incelemesinde bulunan kusurun testi ve altı bulgu içinde teslime en yakın
        // olanı. Göreli dönemler `DateTime.Now` ile hesaplanıyordu, yani SUNUCUNUN yerel
        // saatiyle. Geliştirme makinesi Türkiye saatinde olduğu için hiç görünmedi;
        // teslim Linux konteynerinde yapılıyor ve orada yerel saat UTC'dir.
        //
        // Türkiye UTC+3 olduğundan sonucu şuydu: gece 01:30'da sorulan "bugünkü ciro"
        // sunucu için hâlâ dün → dünün cirosu dönüyor. Daha sinsisi, her gün 00:00-03:00
        // arasındaki kayıtlar sürekli olarak "dün"e sayılıyor — yani günlük toplamlar
        // yalnız gece yarısı değil, HER ZAMAN o üç saatlik dilim kadar yanlış.
        Assert.True(RelativePeriod.Configure("Europe/Istanbul"));

        var utc = DateTime.UtcNow;
        var business = RelativePeriod.Now;

        // Türkiye 2016'dan beri sabit UTC+3 (yaz saati uygulaması yok).
        var fark = business - utc;

        Assert.InRange(fark.TotalHours, 2.9, 3.1);
    }

    [Fact(DisplayName = "Tanınmayan saat dilimi SESSİZCE UTC'ye düşmüyor")]
    public void Configure_RejectsUnknownZone()
    {
        // Sessizce UTC'ye düşmek, yanlış güne bakmanın görünmeyen hâli olurdu. Açılış
        // denetimi bu false'u istisnaya çeviriyor (bkz. Program.cs).
        Assert.False(RelativePeriod.Configure("Mars/Olympus_Mons"));

        // Ve reddedilen değer yerleşmiyor: önceki dilim korunuyor.
        RelativePeriod.Configure("Europe/Istanbul");
        Assert.Equal("Europe/Istanbul", RelativePeriod.BusinessZone.Id);
    }

    [Fact]
    public void Tokens_AreAllResolvable()
    {
        // Modele istemde sunulan her etiket gerçekten çözümlenebilmeli; listeye yeni bir
        // etiket eklenip switch'e eklenmezse model onu seçer ve sorgu 400 döner.
        foreach (var token in RelativePeriod.Tokens)
            Assert.True(RelativePeriod.TryResolve(token, Now, out _, out _),
                $"'{token}' Tokens listesinde ama çözümlenemiyor.");
    }
}
