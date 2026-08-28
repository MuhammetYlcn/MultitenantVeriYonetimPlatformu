# API imajı — iki aşamalı.
#
# Neden iki aşama: derlemek için .NET SDK'sı (~800 MB, derleyici + NuGet) gerekiyor, ama
# ÇALIŞTIRMAK için yalnız çalışma zamanı (~220 MB) yeterli. Tek aşamada yazılsaydı teslim
# edilen imaj derleyiciyi, kaynak kodu ve NuGet önbelleğini de taşırdı — hem gereksiz büyük
# hem de saldırı yüzeyi geniş olurdu (imaja giren birinin elinde kaynak kodun tamamı olur).
# Aşağıdaki `--from=build` yalnızca derlemenin ÇIKTISINI ikinci aşamaya taşıyor.

# ---------------------------------------------------------------------------
# 1. AŞAMA — derleme
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Önce YALNIZ .csproj kopyalanıp restore ediliyor, sonra kaynak kod.
# Sebebi Docker'ın katman önbelleği: her COPY bir katman, ve bir katman değişince
# ondan sonraki her şey yeniden koşar. Kaynak kod her commit'te değişir, paket listesi
# ayda bir. Bu sıralamayla `dotnet restore` (ağdan paket indiren, en yavaş adım) yalnız
# .csproj değiştiğinde koşuyor; sıradan bir kod değişikliğinde önbellekten geliyor.
COPY src/VeriYonetim.Api/VeriYonetim.Api.csproj src/VeriYonetim.Api/
RUN dotnet restore src/VeriYonetim.Api/VeriYonetim.Api.csproj

COPY src/ src/
RUN dotnet publish src/VeriYonetim.Api/VeriYonetim.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ---------------------------------------------------------------------------
# 2. AŞAMA — çalıştırma
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0

# YAZI TİPİ. Belge okuma akışı, şemalı geçişte görüntünün üstüne kolon adlarını yazan bir
# şerit çiziyor (bkz. DocumentVisionService) ve yazı tipini işletim sisteminden istiyor.
# Windows'ta bir sürü yazı tipi hazır gelir; bu temel imajda HİÇ YOKTUR. Kod eksik yazı
# tipinde belgeyi reddetmiyor, şeridi atlayıp uyarı logluyor — yani paket kurulmazsa
# özellik konteynerde SESSİZCE devre dışı kalır, hata da vermez. Fark edilmesi zor bir
# gerileme olurdu; paket bu yüzden burada.
#
# --no-install-recommends + apt listelerinin silinmesi: ikisi de imaj boyutu için.
RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Dinlenecek port. Temel imajın varsayılanı 8080; 5000 seçildi ki geliştirmedeki adres
# (launchSettings) ile teslimdeki adres aynı kalsın — belgelerdeki, Swagger bağlantısındaki
# ve compose'daki tek bir sayı.
#
# ASPNETCORE_URLS değil ASPNETCORE_HTTP_PORTS: ikisi de çalışıyor, ama URLS temel imajın
# kendi HTTP_PORTS ayarını EZDİĞİ için .NET her açılışta "Overriding HTTP_PORTS '8080'"
# uyarısı basıyordu. Teslim edilen kurulumun günlüğünde açıklama isteyen ama hiçbir şey
# ifade etmeyen bir uyarı bırakmamak için doğrudan aynı ayar yazılıyor.
ENV ASPNETCORE_HTTP_PORTS=5000
EXPOSE 5000

# ROOT OLMAYAN KULLANICI. Temel imajda hazır gelen `app` kullanıcısı (UID 1654).
# Konteyner içinde root koşmak, konteynerden kaçış zafiyetlerinde ilk basamağı hediye
# etmek demek; uygulamanın dosya sistemine yazma ihtiyacı da yok (belge görüntüleri
# dahil her şey veritabanında duruyor), yani ayrıcalığın hiçbir karşılığı yok.
USER app

ENTRYPOINT ["dotnet", "VeriYonetim.Api.dll"]
