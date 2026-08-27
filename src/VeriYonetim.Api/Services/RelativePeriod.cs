namespace VeriYonetim.Api.Services;

// Doğal dildeki göreli zaman ifadelerinin ("bu yıl", "geçen ay") mutlak tarih aralığına
// çevrimi.
//
// Hesabı neden model değil de sunucu yapıyor? İki sebep: (1) dil modeli bugünün tarihini
// bilmez — eğitim verisinde donmuştur; (2) bilse bile tarih aritmetiğinde ("geçen çeyrek")
// sık yanılır ve yanıldığında bunu söylemez. Model yalnızca aşağıdaki etiketlerden birini
// SEÇER; aralığı burası hesaplar. Böylece tarih hatası bir olasılık olmaktan çıkar.
//
// Aralık yarı açıktır: [Start, End). Ayın son gününe ait saat 14:00 kaydı da kapsansın
// diye bitiş "<=" ile değil "<" ile karşılaştırılır — aksi halde gün içi saatleri olan
// satırlar sessizce dışarıda kalırdı.
public static class RelativePeriod
{
    // Modele istemde sunulan etiket listesi. Buraya eklenen bir etiket otomatik olarak
    // hem doğrulamada hem istemde geçerli olur (tek kaynak).
    public static readonly IReadOnlyList<string> Tokens = new[]
    {
        "bugun", "dun",
        "buHafta", "gecenHafta",
        "son7Gun", "son30Gun", "son90Gun",
        "buAy", "gecenAy", "son12Ay",
        "buCeyrek", "gecenCeyrek",
        "buYil", "gecenYil"
    };

    /// <summary>
    /// İş saati dilimi — "bugün"ün nerede başlayıp bittiğini belirleyen şey.
    ///
    /// Varsayılan <see cref="TimeZoneInfo.Local"/> DEĞİL, çünkü kusur tam oradaydı:
    /// dönem sınırları `DateTime.Now` ile, yani SUNUCUNUN yerel saatiyle hesaplanıyordu.
    /// Geliştirme makinesi Türkiye saatinde olduğu için bu hiç görünmedi; teslim ise Linux
    /// konteynerinde yapılıyor ve orada yerel saat UTC'dir. Türkiye UTC+3 olduğundan:
    ///
    ///   • Gece 01:30'da sorulan "bugünkü ciro" sunucu için hâlâ dün → DÜNÜN cirosu döner.
    ///   • Daha sinsisi: her gün 00:00-03:00 arasında yapılan kayıtlar "bugün"e değil
    ///     "dün"e sayılır. Yani günlük toplamlar yalnız gece yarısı değil, SÜREKLİ olarak
    ///     o üç saatlik dilim kadar yanlış olur.
    ///
    /// Aynı kayma izleyicilerde de vardı ve orada daha da gizliydi: e-postadaki zaman
    /// damgası ayarlanmış saat diliminden (<c>Email:TimeZone</c>) yazıldığı için kullanıcı
    /// "27.08 01:30" damgalı bir uyarıda dünün rakamını görüyor ve tutarsızlığı fark
    /// edemiyordu.
    ///
    /// Ayar <c>App:TimeZone</c> (IANA adı). Açılışta bir kez kuruluyor; bulunamazsa UTC'ye
    /// düşülüyor ve uyarı veriliyor — sessizce yanlış güne düşmektense sebebi görünsün.
    /// </summary>
    public static TimeZoneInfo BusinessZone { get; private set; } = TimeZoneInfo.Utc;

    /// <summary>Açılışta çağrılır. Bulunamayan dilimde false döner (çağıran uyarır).</summary>
    public static bool Configure(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId)) return false;

        try
        {
            BusinessZone = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// İş saat dilimindeki "şu an". Dönem sınırları BUNUNLA hesaplanmalı —
    /// <c>DateTime.Now</c> ile değil (bkz. <see cref="BusinessZone"/>).
    /// </summary>
    public static DateTime Now =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BusinessZone);

    // now dışarıdan verilir: saf fonksiyon olsun ve sabit bir "bugün" ile test edilebilsin.
    public static bool TryResolve(string? token, DateTime now, out DateTime start, out DateTime end)
    {
        start = default;
        end = default;
        if (string.IsNullOrWhiteSpace(token)) return false;

        // Saat bileşeni atılır; tüm hesaplar gün sınırından başlar. Kind da sıfırlanır:
        // satırlardaki tarihler "timestamp without time zone" olarak yazıldığından
        // Kind=Utc taşıyan bir değeri Npgsql o kolona yazmayı reddeder.
        var today = DateTime.SpecifyKind(now.Date, DateTimeKind.Unspecified);

        // Hafta pazartesi başlar (Türkçe takvim). DayOfWeek pazardan saydığı için kaydırılır.
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var quarterStart = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        var yearStart = new DateTime(today.Year, 1, 1);

        switch (token.Trim())
        {
            case "bugun": start = today; end = today.AddDays(1); return true;
            case "dun": start = today.AddDays(-1); end = today; return true;

            case "buHafta": start = weekStart; end = weekStart.AddDays(7); return true;
            case "gecenHafta": start = weekStart.AddDays(-7); end = weekStart; return true;

            // "Son N gün" bugünü DE kapsar: son7Gun = bugün dahil 7 gün (bugün-6 … bugün).
            case "son7Gun": start = today.AddDays(-6); end = today.AddDays(1); return true;
            case "son30Gun": start = today.AddDays(-29); end = today.AddDays(1); return true;
            case "son90Gun": start = today.AddDays(-89); end = today.AddDays(1); return true;

            case "buAy": start = monthStart; end = monthStart.AddMonths(1); return true;
            case "gecenAy": start = monthStart.AddMonths(-1); end = monthStart; return true;
            case "son12Ay": start = monthStart.AddMonths(-11); end = monthStart.AddMonths(1); return true;

            case "buCeyrek": start = quarterStart; end = quarterStart.AddMonths(3); return true;
            case "gecenCeyrek": start = quarterStart.AddMonths(-3); end = quarterStart; return true;

            case "buYil": start = yearStart; end = yearStart.AddYears(1); return true;
            case "gecenYil": start = yearStart.AddYears(-1); end = yearStart; return true;

            default: return false;
        }
    }
}
