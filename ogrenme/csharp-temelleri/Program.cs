// GÜN 3 — C# Dil Temelleri (Java/Python/C bilgisiyle hızlı geçiş)
// Çalıştırmak için: dotnet run

Console.WriteLine("=== 1. Top-level statements ===");
// Java'da her şey bir class + public static void main(String[] args) içinde olmak zorunda.
// C# 9+'da "top-level statements" var: dosyanın kendisi Main metodu gibi çalışır (bu dosya tam olarak öyle).
// Büyük projelerde (ASP.NET Core Program.cs) genelde bu stil kullanılır.

Console.WriteLine();
Console.WriteLine("=== 2. Değişkenler, tipler, string interpolation ===");
int sayi = 42;                      // Java'daki int ile aynı — value type (struct), stack'te.
string isim = "Ahmet";              // string = System.String, class'tır (Java String gibi reference type).
var otomatikTip = 3.14;             // 'var' derleme zamanında tip çıkarımı yapar (Java'da 'var' Java 10+'da benzer, ama C#'ta çok daha yaygın kullanılır).
Console.WriteLine($"İsim: {isim}, Sayı: {sayi}, Pi: {otomatikTip}");
// $"..." = string interpolation. Java'daki String.format("İsim: %s", isim) yerine geçer.
// Java 21'in text block + STR şablonları gibi ama dil seviyesinde, her yerde kullanılabilir.

Console.WriteLine();
Console.WriteLine("=== 3. Nullable reference types (Java'dan en büyük fark) ===");
// Java'da her reference type (String, List, vs.) HER ZAMAN null olabilir, derleyici uyarmaz.
// C#'ta (nullable enable açıkken, .csproj'da varsayılan) string ile string? FARKLI anlamlara gelir:
string kesinVarOlan = "bu asla null olamaz";      // derleyici null atamana izin vermez
string? olabilirNull = null;                       // ? işareti "bu null olabilir" demek — açıkça belirtmen gerekir
Console.WriteLine(kesinVarOlan.Length);             // güvenli, derleyici garanti eder
if (olabilirNull != null)
{
    Console.WriteLine(olabilirNull.Length);
}
// Bu, Optional<T> kullanmak zorunda kalmadan Java'daki NullPointerException sınıfı hataları
// derleme zamanında yakalamanı sağlar. ASP.NET Core'da DTO'lar bu yüzden çok net olur.

Console.WriteLine();
Console.WriteLine("=== 4. Properties — Java'nın getter/setter'ına C#'ın cevabı ===");
var kullanici = new Kullanici("Ayşe", 30);
Console.WriteLine($"{kullanici.Isim} - {kullanici.Yas}");
kullanici.Yas = 31; // Setter'ı elle yazmadan direkt property'ye atama yapılabiliyor
Console.WriteLine($"Yeni yaş: {kullanici.Yas}");

Console.WriteLine();
Console.WriteLine("=== 5. record — DTO'lar için (Java 14+ record'a çok benzer) ===");
var tenant1 = new TenantDto("acme-corp", "Acme A.Ş.");
var tenant2 = new TenantDto("acme-corp", "Acme A.Ş.");
Console.WriteLine($"tenant1 == tenant2: {tenant1 == tenant2}"); // record'larda value equality otomatik (Java record gibi)
Console.WriteLine(tenant1);

Console.WriteLine();
Console.WriteLine("=== 6. Collections & LINQ (Java Stream API'nin C# karşılığı) ===");
List<int> sayilar = new() { 5, 12, 8, 3, 19, 7 };
// Java: sayilar.stream().filter(n -> n > 7).map(n -> n * 2).collect(Collectors.toList())
var sonuc = sayilar.Where(n => n > 7).Select(n => n * 2).ToList();
Console.WriteLine(string.Join(", ", sonuc));
// LINQ query sözdizimi de var (SQL'e benzer), ama method chain (yukarıdaki gibi) çok daha yaygın kullanılır.

Console.WriteLine();
Console.WriteLine("=== 7. async/await (Java'nın CompletableFuture'ından farklı) ===");
string veri = await VeriGetirAsync();
Console.WriteLine(veri);
// Java'da: CompletableFuture<String> future = ...; future.thenApply(...);  (callback tabanlı, iç içe geçebilir)
// C#'ta: await, senkron kod gibi OKUNUR ama thread'i bloklamaz. ASP.NET Core'da HER ŞEY async olacak
// (DB sorguları, HTTP çağrıları) — Task<T> dönen metodlar Java'daki CompletableFuture<T>'nin karşılığı.

Console.WriteLine();
Console.WriteLine("=== 8. Exception handling (Java'ya çok benzer) ===");
try
{
    RiskliIslem(-5);
}
catch (ArgumentException ex)
{
    Console.WriteLine($"Hata yakalandı: {ex.Message}");
}
// Fark: C#'ta CHECKED EXCEPTION yok. Java'daki "throws IOException" zorunluluğu C#'ta yok,
// hiçbir exception'ı deklare etmek zorunda değilsin. Bu ASP.NET Core'da exception middleware'i
// önemli kılıyor (Gün 4'te göreceğiz).

Console.WriteLine();
Console.WriteLine("=== ALIŞTIRMALAR — Exercises.cs dosyasına bak ===");
Exercises.Calistir();


// --- Local function'lar top-level statement'ların bir parçası sayılır, tip tanımlarından ÖNCE durmalı ---

async Task<string> VeriGetirAsync()
{
    await Task.Delay(200); // örn. bir DB sorgusu ya da HTTP çağrısını simüle ediyor
    return "Async'ten dönen veri";
}

void RiskliIslem(int deger)
{
    if (deger < 0)
        throw new ArgumentException("Değer negatif olamaz");
    Console.WriteLine($"İşlendi: {deger}");
}

// --- class/record gibi tip tanımları dosyanın en sonunda olmalı (C# kuralı: top-level statement'lardan sonra) ---

class Kullanici
{
    public string Isim { get; set; }   // auto-property: Java'da private field + getIsim()/setIsim() yazmana gerek yok
    public int Yas { get; set; }

    public Kullanici(string isim, int yas)
    {
        Isim = isim;
        Yas = yas;
    }
}

// record: immutable veri taşıyıcılar için (Java'nın "record Tenant(String slug, String ad) {}" ile birebir aynı fikir)
record TenantDto(string Slug, string Ad);
