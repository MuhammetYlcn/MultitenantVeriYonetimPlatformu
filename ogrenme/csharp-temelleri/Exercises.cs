// Gün 3 alıştırmaları. Her metodu TODO'ya göre doldur, dotnet run ile çalıştırıp sonucu kontrol et.
// Java'dan geliyorsan mantık aynı, sözdizimi farklı — Program.cs'teki örneklere bakarak ilerle.

static class Exercises
{
    public static void Calistir()
    {
        Console.WriteLine("--- Alıştırma 1: ÇiftSayilariTopla ---");
        Console.WriteLine(ÇiftSayilariTopla(new List<int> { 1, 2, 3, 4, 5, 6 })); // beklenen: 12

        Console.WriteLine("--- Alıştırma 2: EnUzunIsim ---");
        Console.WriteLine(EnUzunIsim(new List<string> { "Ali", "Muhammet", "Ayşe" })); // beklenen: Muhammet

        Console.WriteLine("--- Alıştırma 3: GuvenliUzunluk (nullable) ---");
        Console.WriteLine(GuvenliUzunluk(null));       // beklenen: 0
        Console.WriteLine(GuvenliUzunluk("merhaba"));   // beklenen: 7

        Console.WriteLine("--- Alıştırma 4: UrunOlustur (record) ---");
        var urun = UrunOlustur("Klavye", 450.0m);
        Console.WriteLine(urun);

        Console.WriteLine("--- Alıştırma 5: ToplamiHesaplaAsync ---");
        // Bu satırı Program.cs'te "await Exercises.Calistir();" yapmadığımız için burada senkron çağırıyoruz.
        // Kendi denemen için: yukarıdaki Calistir metodunu async Task yapıp await ile çağırabilirsin (opsiyonel, ileri seviye).
        var toplamTask = ToplamiHesaplaAsync(new List<int> { 10, 20, 30 });
        toplamTask.Wait();
        Console.WriteLine(toplamTask.Result); // beklenen: 60
    }

    // 1) LINQ kullanarak listedeki çift sayıların toplamını döndür.
    //    İpucu: .Where(...).Sum()
    static int ÇiftSayilariTopla(List<int> sayilar)
    {
        var ciftler=sayilar.Where(n=>n %2==0).ToList();
        int sonuc=ciftler.Sum();
        return sonuc;
    }

    // 2) LINQ kullanarak en uzun ismi döndür.
    //    İpucu: .OrderByDescending(...).First()  ya da  .MaxBy(...)
    static String EnUzunIsim(List<string> isimler)
    {
        var temp=isimler[0];
        var enuzun=isimler.Where(n=> n.Length > temp.Length).ToList();
        if (enuzun.Count() < 1)
        {
            return temp;
        }
        else
        {
            return EnUzunIsim(enuzun);
        }
    }

    // 3) Nullable string parametresi al. null ise 0, değilse .Length döndür.
    //    İpucu: metot imzasında "string?" kullanmayı unutma.
    static int GuvenliUzunluk(string? metin)
    {
        if (metin == null)
        {
            return 0;
        }
        else
        {
            return metin.Length;
        }
    }

    // 4) Aşağıda tanımlı Urun record'ından bir örnek oluştur ve döndür.
    static Urun UrunOlustur(string ad, decimal fiyat)
    {
        var urun1=new Urun(ad,fiyat);
        return urun1;
    }

    // 5) async/await kullanarak (Task.Delay ile gerçek bir işlemi simüle ederek) listenin toplamını hesapla.
    //    İpucu: Program.cs'teki VeriGetirAsync örneğine bak.
    static async Task<int> ToplamiHesaplaAsync(List<int> sayilar)
    {
        await Task.Delay(100);
        int toplam=sayilar.Sum();
        return toplam;
    }
}

// decimal: parasal değerler için kullanılır (Java'da BigDecimal'a denk gelir, ama dil-seviyesinde,
// + - * gibi operatörleri normal sayı gibi kullanabilirsin — BigDecimal.add() gibi metod çağırmana gerek yok).
record Urun(string Ad, decimal Fiyat);
