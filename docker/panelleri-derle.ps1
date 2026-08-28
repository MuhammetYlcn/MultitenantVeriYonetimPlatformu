# İki paneli de web için derler. `docker compose build`den ÖNCE koşturulur.
#
# Neden ayrı bir adım: panel imajları Flutter SDK'sı içermiyor, yalnızca derlenmiş
# `build/web` çıktısını bir web sunucusuna kopyalıyorlar (gerekçe: docker/panel.Dockerfile).
# Bunun bedeli, derlemenin imajın dışında kalması — bu betik o adımı tek komuta indiriyor.
#
# Kullanım (depo kökünden):
#   powershell -ExecutionPolicy Bypass -File docker\panelleri-derle.ps1

$ErrorActionPreference = 'Stop'

# Flutter PATH'te olmayabilir (bu makinede değil), o yüzden önce PATH'e bakılıp
# bulunamazsa bilinen kurulum yeri deneniyor. Bulunamazsa betik SESSİZCE geçmiyor:
# derlenmemiş panelle devam etmek, `docker compose build`in anlamsız bir COPY hatasıyla
# düşmesi demek olurdu.
$flutter = (Get-Command flutter -ErrorAction SilentlyContinue).Source
if (-not $flutter) {
    $aday = 'C:\development\flutter\bin\flutter.bat'
    if (Test-Path $aday) {
        $flutter = $aday
    } else {
        throw "Flutter bulunamadı. PATH'e ekleyin ya da bu betikteki `$aday yolunu düzeltin."
    }
}

# Betik nerede olursa olsun depo kökünden çalışsın.
$kok = Split-Path -Parent $PSScriptRoot

foreach ($panel in @('client', 'admin')) {
    $dizin = Join-Path $kok $panel
    Write-Host "`n=== $panel derleniyor ===" -ForegroundColor Cyan

    Push-Location $dizin
    try {
        # --release: küçültülmüş ve hızlı çıktı. Varsayılan (debug) çıktı hem çok daha
        # büyük hem de tarayıcıda gözle görülür yavaş — teslim edilen imaja girmemeli.
        & $flutter build web --release
        if ($LASTEXITCODE -ne 0) { throw "$panel derlenemedi (çıkış kodu $LASTEXITCODE)." }
    }
    finally {
        Pop-Location
    }
}

Write-Host "`nHazır. Sıradaki adım:" -ForegroundColor Green
Write-Host "  docker compose up -d --build"
