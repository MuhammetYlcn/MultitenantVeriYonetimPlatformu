#!/bin/sh
# Panelin API adresini ÇALIŞMA ANINDA yazar.
#
# Sorun şuydu: adres Dart kodunda sabitti (`http://localhost:5000`). Flutter web'de bu
# adres derleme anında dosyaya gömülür, yani imaj bir kez derlendikten sonra başka bir
# sunucuda kullanılamaz — teslim alan kişi adresi değiştirmek için Flutter SDK'sı kurup
# yeniden derlemek zorunda kalırdı. `--dart-define` de aynı kapıya çıkardı: değer yine
# derleme anında gömülür.
#
# Çözüm: adres, Dart'ın derleme anında değil TARAYICININ SAYFA AÇILIRKEN okuduğu küçük bir
# JS dosyasından geliyor. Dosyayı bu betik konteyner her açılışta yeniden yazıyor, yani
# adres bir ortam değişkeni (API_BASE_URL) — imaj aynı kalır, adres değişir.
#
# Varsayılan neden `window.location.origin`: teslim kurulumunda nginx /api ve /hubs
# isteklerini API konteynerine kendisi iletiyor (bkz. panel.nginx.conf), yani panel ile API
# tarayıcı gözünde AYNI ADRESTE. Sayfanın kendi adresini kullanmak, kurulumu yapan kişinin
# hiçbir şey ayarlamadan doğru sonucu almasını sağlıyor — makine adı, port, alan adı ne
# olursa olsun. API_BASE_URL yalnız paneller ile API ayrı adreslerde yayınlanacaksa gerekir.
set -e

: "${API_BASE_URL:=}"

# Değer çift tırnak içine yazılıyor; boşsa JS'te "" (yanlış değer) olur ve `||` sağdaki
# ifadeye düşer. Yani "ayar verilmedi" = "sayfanın kendi adresi".
cat > /usr/share/nginx/html/config.js <<EOF
// BU DOSYA KONTEYNER AÇILIŞINDA ÜRETİLİR — elle yapılan değişiklik ilk yeniden
// başlatmada kaybolur. Adresi değiştirmek için API_BASE_URL ortam değişkenini kullanın.
window.API_BASE_URL = "${API_BASE_URL}" || window.location.origin;
EOF

echo "panel: API adresi -> ${API_BASE_URL:-<sayfanın kendi adresi>}"
