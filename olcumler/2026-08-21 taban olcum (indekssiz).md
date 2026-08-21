# Ölçek ölçümü — 21.08.2026 17:45

- **PostgreSQL:** 16.14 (Debian 16.14-1.pgdg13+1)
- **Tablodaki toplam satır:** 2.225.000
- **Veri setleri:** 10 set / 3 firma
- **Tablo boyutu:** 1681 MB
- **İşlemci / çekirdek:** 16 mantıksal çekirdek
- **Uç ölçümü:** http://localhost:5099
- **DatasetRows indeksleri:** IX_DatasetRows_DatasetId, PK_DatasetRows

Süreler **medyan**, milisaniye. Isınma koşuları sayılmadı.

## Veritabanı sorguları

Sorguların SQL'i uygulamanın kendi builder'ları tarafından üretildi (`DatasetRowQueryBuilder`, `DatasetAggregateQueryBuilder`) — elle yazılmadı.

| senaryo | ne ölçülüyor | 10k | 100k | 1M |
|---|---|---:|---:|---:|
| `satir_ilk_sayfa` | İlk sayfa, tarihe göre sıralı | 9,9 | 64,6 | 491 |
| `satir_son_sayfa` | Son sayfa (derin OFFSET) | 11,3 | 81,2 | 711 |
| `filtre_metin` | sehir = Ankara | 4,3 | 32,4 | 225 |
| `filtre_metin_icinde` | urun içinde 'kablo' | 6,1 | 46,3 | 358 |
| `filtre_sayi` | tutar >= 150000 | 2,8 | 26,4 | 232 |
| `filtre_donem` | tarih son 90 gün | 4,7 | 35,6 | 306 |
| `ozet_sehir` | Şehre göre toplam tutar | 4,1 | 65,5 | 775 |
| `ozet_iki_anahtar` | Şehir × kategori toplam tutar | 5,9 | 81,4 | 995 |
| `zaman_serisi_ay` | Aylara göre toplam tutar | 9,6 | 75,2 | 759 |
| `medyan_sehir` | Şehre göre medyan tutar | 9,2 | 102 | 1.166 |
| `farkli_musteri` | Kaç farklı müşteri (gruplamasız) | 12,8 | 101 | 1.092 |
| `ozet_filtreli` | Son 90 günün kategori özeti | 5,6 | 40,4 | 288 |
| `join_segment` | Müşteri segmentine göre toplam tutar (2 set) | 22,3 | 109 | 560 |

### Erişim biçimi (EXPLAIN)

| senaryo | 10k | 100k | 1M |
|---|---|---|---|
| `satir_ilk_sayfa` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `satir_son_sayfa` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `filtre_metin` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `filtre_metin_icinde` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `filtre_sayi` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `filtre_donem` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `ozet_sehir` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `ozet_iki_anahtar` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `zaman_serisi_ay` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `medyan_sehir` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `farkli_musteri` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `ozet_filtreli` | Index Scan | Bitmap Heap Scan | Seq Scan |
| `join_segment` | Index Scan | Bitmap Heap Scan + Index Scan | Seq Scan + Bitmap Heap Scan |

## HTTP uçları

Tarayıcının çağırdığı adresler, gerçek oturum token'ıyla. Satır listesi ucu her çağrıda ayrıca `COUNT(*)` de koşturur (sayfa sayısı için).

| senaryo | ne ölçülüyor | 10k | 100k | 1M |
|---|---|---:|---:|---:|
| `uc_satir_ilk_sayfa` | GET rows — ilk sayfa | 14,3 | 65,9 | 338 |
| `uc_satir_son_sayfa` | GET rows — son sayfa | 15,8 | 114 | 756 |
| `uc_filtre_metin` | GET rows — sehir = Ankara | 8,0 | 30,4 | 181 |
| `uc_ozet_sehir` | GET aggregate — şehre göre toplam | 9,6 | 58,5 | 545 |
| `uc_zaman_serisi` | GET aggregate — aylık toplam | 13,9 | 69,6 | 660 |

## Veri yazma

İki yol da BOŞ bir veri setine aynı sayıda satır yazıyor. Aynı işi yapmıyorlar: uç CSV'yi ayrıştırıp şemaya göre doğruluyor, `COPY` hazır değer basıyor. Karşılaştırmanın amacı da bu — doğrulamanın mı, yazmanın mı pahalı olduğu.

| yol | satır | süre (sn) | satır/sn | not |
|---|---:|---:|---:|---|
| uç — 4 komşu set | 1.000 | 5,5 | 180 | 0,1 MB CSV |
| uç — komşusuz firma | 1.000 | 0,1 | 8.493 | 0,1 MB CSV |
| COPY (ikili akış) | 1.000 | 0,1 | 16.198 | doğrulama ve CSV ayrıştırma yok |
| uç — 4 komşu set | 10.000 | 6,2 | 1.624 | 0,8 MB CSV |
| uç — komşusuz firma | 10.000 | 0,6 | 18.045 | 0,8 MB CSV |
| COPY (ikili akış) | 10.000 | 0,2 | 60.845 | doğrulama ve CSV ayrıştırma yok |
| uç — 4 komşu set | 50.000 | 8,2 | 6.075 | 4,2 MB CSV |
| uç — komşusuz firma | 50.000 | 2,7 | 18.330 | 4,2 MB CSV |
| COPY (ikili akış) | 50.000 | 0,5 | 97.348 | doğrulama ve CSV ayrıştırma yok |

## Ayrıntı

| senaryo | nokta | ölçek | medyan | en kötü | dönen satır |
|---|---|---|---:|---:|---:|
| `satir_ilk_sayfa` | SQL | 10k | 9,9 | 10,4 | 25 |
| `satir_son_sayfa` | SQL | 10k | 11,3 | 12,4 | 25 |
| `filtre_metin` | SQL | 10k | 4,3 | 4,6 | 25 |
| `filtre_metin_icinde` | SQL | 10k | 6,1 | 6,4 | 25 |
| `filtre_sayi` | SQL | 10k | 2,8 | 3,1 | 25 |
| `filtre_donem` | SQL | 10k | 4,7 | 5,1 | 25 |
| `ozet_sehir` | SQL | 10k | 4,1 | 4,3 | 20 |
| `ozet_iki_anahtar` | SQL | 10k | 5,9 | 6,8 | 50 |
| `zaman_serisi_ay` | SQL | 10k | 9,6 | 10,2 | 37 |
| `medyan_sehir` | SQL | 10k | 9,2 | 9,9 | 20 |
| `farkli_musteri` | SQL | 10k | 12,8 | 14,3 | 1 |
| `ozet_filtreli` | SQL | 10k | 5,6 | 6,2 | 8 |
| `join_segment` | SQL | 10k | 22,3 | 23,1 | 4 |
| `uc_satir_ilk_sayfa` | uç | 10k | 14,3 | 18,1 | 25 |
| `uc_satir_son_sayfa` | uç | 10k | 15,8 | 17,3 | 25 |
| `uc_filtre_metin` | uç | 10k | 8,0 | 8,9 | 25 |
| `uc_ozet_sehir` | uç | 10k | 9,6 | 10,2 | 20 |
| `uc_zaman_serisi` | uç | 10k | 13,9 | 16,0 | 37 |
| `satir_ilk_sayfa` | SQL | 100k | 64,6 | 68,6 | 25 |
| `satir_son_sayfa` | SQL | 100k | 81,2 | 89,9 | 25 |
| `filtre_metin` | SQL | 100k | 32,4 | 33,5 | 25 |
| `filtre_metin_icinde` | SQL | 100k | 46,3 | 49,7 | 25 |
| `filtre_sayi` | SQL | 100k | 26,4 | 28,1 | 25 |
| `filtre_donem` | SQL | 100k | 35,6 | 37,2 | 25 |
| `ozet_sehir` | SQL | 100k | 65,5 | 69,2 | 20 |
| `ozet_iki_anahtar` | SQL | 100k | 81,4 | 88,9 | 50 |
| `zaman_serisi_ay` | SQL | 100k | 75,2 | 84,4 | 37 |
| `medyan_sehir` | SQL | 100k | 102 | 109 | 20 |
| `farkli_musteri` | SQL | 100k | 101 | 124 | 1 |
| `ozet_filtreli` | SQL | 100k | 40,4 | 42,0 | 8 |
| `join_segment` | SQL | 100k | 109 | 121 | 4 |
| `uc_satir_ilk_sayfa` | uç | 100k | 65,9 | 75,2 | 25 |
| `uc_satir_son_sayfa` | uç | 100k | 114 | 123 | 25 |
| `uc_filtre_metin` | uç | 100k | 30,4 | 36,6 | 25 |
| `uc_ozet_sehir` | uç | 100k | 58,5 | 60,5 | 20 |
| `uc_zaman_serisi` | uç | 100k | 69,6 | 97,1 | 37 |
| `satir_ilk_sayfa` | SQL | 1M | 491 | 663 | 25 |
| `satir_son_sayfa` | SQL | 1M | 711 | 850 | 25 |
| `filtre_metin` | SQL | 1M | 225 | 266 | 25 |
| `filtre_metin_icinde` | SQL | 1M | 358 | 422 | 25 |
| `filtre_sayi` | SQL | 1M | 232 | 262 | 25 |
| `filtre_donem` | SQL | 1M | 306 | 319 | 25 |
| `ozet_sehir` | SQL | 1M | 775 | 821 | 20 |
| `ozet_iki_anahtar` | SQL | 1M | 995 | 1.122 | 50 |
| `zaman_serisi_ay` | SQL | 1M | 759 | 831 | 37 |
| `medyan_sehir` | SQL | 1M | 1.166 | 1.326 | 20 |
| `farkli_musteri` | SQL | 1M | 1.092 | 1.140 | 1 |
| `ozet_filtreli` | SQL | 1M | 288 | 328 | 8 |
| `join_segment` | SQL | 1M | 560 | 647 | 4 |
| `uc_satir_ilk_sayfa` | uç | 1M | 338 | 366 | 25 |
| `uc_satir_son_sayfa` | uç | 1M | 756 | 875 | 25 |
| `uc_filtre_metin` | uç | 1M | 181 | 204 | 25 |
| `uc_ozet_sehir` | uç | 1M | 545 | 638 | 20 |
| `uc_zaman_serisi` | uç | 1M | 660 | 719 | 37 |

