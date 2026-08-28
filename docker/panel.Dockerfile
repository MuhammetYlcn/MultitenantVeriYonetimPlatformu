# Panel imajı (müşteri paneli ve platform paneli aynı dosyayı kullanır; hangisi olduğu
# PANEL_DIR yapı argümanıyla söylenir).
#
# DERLEME KONTEYNERDE YAPILMIYOR, BİLİNÇLİ. Flutter SDK'sı imaja kurulup `flutter build web`
# konteyner içinde koşsaydı: temel imaj ~1,5 GB büyür, ilk derleme dakikalar sürer ve bu
# bedel her `docker compose build`de yeniden ödenirdi. Oysa `build/web` çıktısı birkaç
# megabaytlık düz statik dosya. O yüzden derleme YERELDE yapılıp çıktısı kopyalanıyor
# (bkz. docker/panelleri-derle.ps1) ve imaj yalnız bir web sunucusundan ibaret kalıyor.
#
# Bunun bedeli: `docker compose build` öncesinde panellerin derlenmiş olması gerekiyor.
# Derlenmemişse COPY "build/web bulunamadı" diye durur — sessiz bir eksik değil.
FROM nginx:1.27-alpine

# Hangi panel? client/ ya da admin/. Compose bunu build.args ile veriyor.
ARG PANEL_DIR

COPY ${PANEL_DIR}/build/web /usr/share/nginx/html
COPY docker/panel.nginx.conf /etc/nginx/conf.d/default.conf

# nginx temel imajı, açılışta /docker-entrypoint.d/ altındaki .sh dosyalarını sunucuyu
# başlatmadan ÖNCE koşturur. Sunucunun adresi buradan yazılıyor (bkz. betiğin kendi
# açıklaması); imajın yeniden derlenmeden başka bir adreste yayınlanabilmesi buna bağlı.
COPY docker/panel-entrypoint.sh /docker-entrypoint.d/40-api-base-url.sh
RUN chmod +x /docker-entrypoint.d/40-api-base-url.sh

EXPOSE 80
