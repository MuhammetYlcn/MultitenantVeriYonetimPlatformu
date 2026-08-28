# Kurulum ve Çalıştırma

Projenin tamamı konteynerde çalışır: veritabanı, API ve iki panel. Aşağıdaki adımlardan
sonra sisteme tarayıcıdan girilebilir.

## Gerekenler

| | neden |
|---|---|
| **Docker Desktop** | beş kabın hepsi buradan kalkıyor |
| **Ollama** + iki model | yapay zekâ **konteynerin dışında**, ana makinede koşuyor (aşağıda) |
| **Flutter SDK** | yalnız panelleri bir kez derlemek için; çalıştırmak için gerekmiyor |

Modeller:

```
ollama pull qwen2.5vl:7b          # belge/fatura okuma
```

Doğal dilde sorgu için kullanılan `veriyonetim-planlayici:7b-k2` bu proje için ince
ayarlanmış modeldir; kurulumu `training/` altında anlatılıyor.

**Ollama neden konteynerde değil:** model GPU istiyor ve Windows'ta Docker'a GPU geçirmek
WSL2 + NVIDIA container toolkit gerektiriyor. API, modele `host.docker.internal:11434`
üzerinden ulaşıyor. Bu, "veri kurum dışına çıkmaz" savunmasını değiştirmiyor — model yine
aynı makinede çalışıyor, hiçbir veri dışarı gitmiyor.

## 1. Ayarlar

`.env.example` dosyasını `.env` adıyla kopyalayın ve `BURAYA-` ile başlayan bütün
değerleri doldurun.

Uygulama, yer tutucusu doldurulmamış ya da zayıf bir sırla **açılmaz** ve sebebini söyler.
Bu bilinçli: sessizce çalışan ama korumayan bir kurulumdansa hiç açılmayan bir kurulum
tercih edildi. İmzalama anahtarı en az 32 karakter olmalı:

```
openssl rand -base64 48
```

## 2. Panelleri derleyin

```
powershell -ExecutionPolicy Bypass -File docker\panelleri-derle.ps1
```

Bu adım bir kez gerekir; panel arayüzünde bir değişiklik yapılmadıkça tekrarlanmaz.
(Derleme neden imajın içinde değil: Flutter SDK'sı imajı ~1,5 GB büyütür ve bedeli her
derlemede yeniden ödenirdi. Çıktı ise birkaç megabaytlık statik dosya.)

## 3. Ayağa kaldırın

```
docker compose up -d --build
```

## 4. Açın

| adres | ne |
|---|---|
| http://localhost:5200 | müşteri paneli |
| http://localhost:5300 | platform yönetim paneli |
| http://localhost:5000 | API |
| http://localhost:8025 | giden e-postalar (Mailpit) |

İlk kullanıcı, müşteri panelindeki **kayıt** ekranından oluşturulur; o kullanıcı kendi
firmasının Admin'i olur ve diğerlerini davet eder. Platform yöneticisinin kayıt ekranı
**yoktur**, kimliği yalnızca `.env`deki `PlatformAdmin__*` değerlerinden gelir.

E-posta gerçekten gönderiliyor ama **dışarı çıkmıyor**: Mailpit hem SMTP sunucusu gibi
davranıp postayı kabul ediyor hem de yakaladığını kendi arayüzünde gösteriyor. Böylece
teslim alan kişinin bir e-posta sağlayıcısına hesap açması ve ayar dosyasına gerçek bir
parola yazması gerekmiyor.

## Başka bir sunucuda yayınlamak

Paneller kendi adreslerini **çalışma anında** okuyor, imaja gömülü değil — makine adı ya
da port değişince yeniden derleme gerekmez. Panellerin önündeki nginx `/api` ve `/hubs`
isteklerini API kabına kendisi ilettiği için, varsayılan hâlde panel hangi adresten
açılıyorsa API de oradan görünür; çoğu kurulumda ayarlanacak bir şey yoktur.

API ayrı bir adreste yayınlanacaksa panel servislerine `API_BASE_URL` ortam değişkeni
verilir, ve o adres API'nin CORS listesine eklenir (`.env` içindeki `PANEL_ORIGIN` /
`ADMIN_ORIGIN`).

Şifreleme (HTTPS) bu kurulumda API'nin işi değil, önündeki vekil sunucunun işidir.

## Sorun giderme

**Kap açılmıyor, "Zorunlu ayar eksik" yazıyor.** `.env` içinde o ayar boş kalmış ya da
`BURAYA-` yer tutucusu duruyor.

**Doğal dilde sorgu cevap vermiyor.** Ollama ana makinede çalışıyor mu:
`curl http://localhost:11434/api/tags`. Konteynerin gördüğü adres `host.docker.internal`;
Linux'ta bu ad compose'daki `extra_hosts` satırıyla tanımlanıyor.

**Panel açılıyor ama giriş yapılamıyor.** Tarayıcının ağ sekmesinde isteklerin gittiği
adrese bakın. `/config.js` dosyası panelin kullandığı adresi gösterir; o dosya kap her
açıldığında yeniden yazılır, elle yapılan değişiklik kalıcı olmaz.

**5200/5300/5000 portu dolu.** Aynı portlarda daha önce elle başlatılmış bir geliştirme
sunucusu kalmış olabilir; kapatın ve `docker compose restart client admin` yapın.
