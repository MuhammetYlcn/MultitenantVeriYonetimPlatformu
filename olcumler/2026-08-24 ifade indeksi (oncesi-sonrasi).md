# Ölçek ölçümü — 24.08.2026 14:46

- **PostgreSQL:** 16.14 (Debian 16.14-1.pgdg13+1)
- **Tablodaki toplam satır:** 2.225.000
- **Veri setleri:** 10 set / 3 firma
- **Tablo boyutu:** 1609 MB
- **İşlemci / çekirdek:** 16 mantıksal çekirdek
- **Uç ölçümü:** http://localhost:5099
- **DatasetRows indeksleri:** IX_DatasetRows_DatasetId, PK_DatasetRows
- **İndeksli fazdaki indeksler:** IX_DatasetRows_DatasetId, PK_DatasetRows, ix_olcum_data_gin, ix_olcum_sehir, ix_olcum_tutar, ix_olcum_urun_trgm

Süreler **medyan**, milisaniye. Isınma koşuları sayılmadı.

## Denenen ifade indeksleri

İndekslenen ifade, sorgunun ürettiği ifadeyle birebir aynı olmalı; farklı olursa PostgreSQL indeksi hiç kullanmaz ve indeks sessizce boşa yatırım olur.

| indeks | hedef senaryo | durum | kurulum | boyut |
|---|---|---|---:|---:|
| `ix_olcum_sehir` | `filtre_metin` | kuruldu | 1,7 sn | 15 MB |
| `ix_olcum_tutar` | `filtre_sayi` | kuruldu | 3,3 sn | 101 MB |
| `ix_olcum_urun_trgm` | `filtre_metin_icinde` | kuruldu | 3,6 sn | 38 MB |
| `ix_olcum_tarih` | `filtre_donem / satir_ilk_sayfa` | **KURULAMADI** — functions in index expression must be marked IMMUTABLE | — | — |
| `ix_olcum_data_gin` | `(bugünkü sorguların hiçbiri)` | kuruldu | 17,0 sn | 201 MB |

**`ix_olcum_sehir`** — Metin eşitliği `lower(...) = lower(@p)` üretiyor (harfe duyarsız arama kararı); indeks de lower() üzerine kurulmalı, ham değere kurulan indeks bu sorguda kullanılmaz.

```sql
CREATE INDEX ix_olcum_sehir ON "DatasetRows"
  ("DatasetId", lower(("Data"->>'sehir')))
```

**`ix_olcum_tutar`** — Sayısal karşılaştırma `(...)::numeric >= @p` üretiyor. text→numeric dönüşümü IMMUTABLE olduğu için indekslenebiliyor.

```sql
CREATE INDEX ix_olcum_tutar ON "DatasetRows"
  ("DatasetId", ((("Data"->>'tutar'))::numeric))
```

**`ix_olcum_urun_trgm`** — `ILIKE '%kablo%'` baştan bağlı olmadığı için b-tree işe yaramaz; üç harfli parçalara (trigram) bakan GIN gerekir. "DatasetId"in aynı indekse girebilmesi için btree_gin uzantısı şart.

```sql
CREATE INDEX ix_olcum_urun_trgm ON "DatasetRows"
  USING gin ("DatasetId", ("Data"->>'urun') gin_trgm_ops)
```

**`ix_olcum_tarih`** — Tarih hem sıralamanın hem dönem filtresinin dayanağı. Bu adayın KURULMASI beklenmiyor: text→timestamp dönüşümü DateStyle ayarına bağlı olduğu için PostgreSQL onu IMMUTABLE saymaz. Denenmesinin sebebi de bu — sınırın nerede olduğunu tahmin değil hata mesajı söylemeli.

```sql
CREATE INDEX ix_olcum_tarih ON "DatasetRows"
  ("DatasetId", ((("Data"->>'tarih'))::timestamp))
```

**`ix_olcum_data_gin`** — Kolon adı bilmeden BÜTÜN jsonb'yi indeksleyen tek genel aday. Bugünkü SQL bundan faydalanamaz, çünkü `@>` değil `->>` kullanıyor. Ölçüme yine de giriyor: maliyeti (boyut, kurulum süresi, yazmaya etkisi) genel bir çözümün bedelini gösteriyor.

```sql
CREATE INDEX ix_olcum_data_gin ON "DatasetRows"
  USING gin ("Data" jsonb_path_ops)
```

## Veritabanı sorguları

Sorguların SQL'i uygulamanın kendi builder'ları tarafından üretildi (`DatasetRowQueryBuilder`, `DatasetAggregateQueryBuilder`) — elle yazılmadı.

| senaryo | ne ölçülüyor | 10k | 100k | 1M |
|---|---|---:|---:|---:|
| `satir_ilk_sayfa` | İlk sayfa, tarihe göre sıralı | 10,1 | 66,5 | 493 |
| `satir_son_sayfa` | Son sayfa (derin OFFSET) | 10,3 | 79,9 | 665 |
| `filtre_metin` | sehir = Ankara | 3,7 | 35,2 | 251 |
| `filtre_metin_icinde` | urun içinde 'kablo' | 6,4 | 48,1 | 337 |
| `filtre_sayi` | tutar >= 150000 | 3,1 | 30,3 | 210 |
| `filtre_donem` | tarih son 90 gün | 5,1 | 37,7 | 272 |
| `ozet_sehir` | Şehre göre toplam tutar | 4,0 | 64,4 | 603 |
| `ozet_iki_anahtar` | Şehir × kategori toplam tutar | 5,1 | 84,9 | 883 |
| `zaman_serisi_ay` | Aylara göre toplam tutar | 7,9 | 60,5 | 651 |
| `medyan_sehir` | Şehre göre medyan tutar | 7,4 | 91,2 | 1.167 |
| `farkli_musteri` | Kaç farklı müşteri (gruplamasız) | 12,0 | 115 | 1.061 |
| `ozet_filtreli` | Son 90 günün kategori özeti | 4,6 | 40,8 | 289 |
| `join_segment` | Müşteri segmentine göre toplam tutar (2 set) | 21,2 | 105 | 519 |

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
| `uc_satir_ilk_sayfa` | GET rows — ilk sayfa | 13,6 | 47,8 | 335 |
| `uc_satir_son_sayfa` | GET rows — son sayfa | 16,5 | 95,4 | 732 |
| `uc_filtre_metin` | GET rows — sehir = Ankara | 9,6 | 33,4 | 212 |
| `uc_ozet_sehir` | GET aggregate — şehre göre toplam | 10,8 | 65,6 | 583 |
| `uc_zaman_serisi` | GET aggregate — aylık toplam | 13,9 | 70,6 | 625 |

## Veri yazma

İki yol da BOŞ bir veri setine aynı sayıda satır yazıyor. Aynı işi yapmıyorlar: uç CSV'yi ayrıştırıp şemaya göre doğruluyor, `COPY` hazır değer basıyor. Karşılaştırmanın amacı da bu — doğrulamanın mı, yazmanın mı pahalı olduğu.

| yol | satır | süre (sn) | satır/sn | not |
|---|---:|---:|---:|---|
| uç — 4 komşu set | 1.000 | 5,5 | 181 | 0,1 MB CSV |
| uç — komşusuz firma | 1.000 | 0,1 | 12.541 | 0,1 MB CSV |
| COPY (ikili akış) | 1.000 | 0,1 | 11.558 | doğrulama ve CSV ayrıştırma yok |
| uç — 4 komşu set | 10.000 | 0,3 | 37.125 | 0,8 MB CSV |
| uç — komşusuz firma | 10.000 | 0,3 | 36.412 | 0,8 MB CSV |
| COPY (ikili akış) | 10.000 | 0,2 | 42.546 | doğrulama ve CSV ayrıştırma yok |
| uç — 4 komşu set | 50.000 | 1,5 | 32.644 | 4,2 MB CSV |
| uç — komşusuz firma | 50.000 | 1,0 | 47.941 | 4,2 MB CSV |
| COPY (ikili akış) | 50.000 | 0,9 | 55.927 | doğrulama ve CSV ayrıştırma yok |

## İndeks öncesi / sonrası

Aynı senaryolar, aynı koşuda, önce indekssiz sonra ifade indeksleriyle. "kat" sütunu kaç kat hızlandığını söyler (1,0 = değişmedi).

### Veritabanı sorguları

| senaryo | 10k öncesi | 10k sonrası | kat | 100k öncesi | 100k sonrası | kat | 1M öncesi | 1M sonrası | kat |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `satir_ilk_sayfa` | 10,1 | 10,8 | 0,9× | 66,5 | 65,8 | 1,0× | 493 | 503 | 1,0× |
| `satir_son_sayfa` | 10,3 | 11,6 | 0,9× | 79,9 | 88,9 | 0,9× | 665 | 697 | 1,0× |
| `filtre_metin` | 3,7 | 1,6 | 2,3× | 35,2 | 13,4 | 2,6× | 251 | 111 | 2,3× |
| `filtre_metin_icinde` | 6,4 | 3,7 | 1,7× | 48,1 | 31,0 | 1,6× | 337 | 316 | 1,1× |
| `filtre_sayi` | 3,1 | 0,6 | 5,4× | 30,3 | 0,6 | 51,4× | 210 | 0,7 | 306,4× |
| `filtre_donem` | 5,1 | 5,6 | 0,9× | 37,7 | 48,5 | 0,8× | 272 | 310 | 0,9× |
| `ozet_sehir` | 4,0 | 4,8 | 0,8× | 64,4 | 75,5 | 0,9× | 603 | 556 | 1,1× |
| `ozet_iki_anahtar` | 5,1 | 5,9 | 0,9× | 84,9 | 92,0 | 0,9× | 883 | 926 | 1,0× |
| `zaman_serisi_ay` | 7,9 | 8,4 | 0,9× | 60,5 | 63,7 | 1,0× | 651 | 643 | 1,0× |
| `medyan_sehir` | 7,4 | 7,9 | 0,9× | 91,2 | 110 | 0,8× | 1.167 | 1.149 | 1,0× |
| `farkli_musteri` | 12,0 | 12,0 | 1,0× | 115 | 119 | 1,0× | 1.061 | 1.148 | 0,9× |
| `ozet_filtreli` | 4,6 | 5,1 | 0,9× | 40,8 | 43,3 | 0,9× | 289 | 306 | 0,9× |
| `join_segment` | 21,2 | 20,0 | 1,1× | 105 | 120 | 0,9× | 519 | 1.144 | 0,5× |

### HTTP uçları

| senaryo | 10k öncesi | 10k sonrası | kat | 100k öncesi | 100k sonrası | kat | 1M öncesi | 1M sonrası | kat |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `uc_satir_ilk_sayfa` | 13,6 | 9,3 | 1,5× | 47,8 | 47,5 | 1,0× | 335 | 339 | 1,0× |
| `uc_satir_son_sayfa` | 16,5 | 12,4 | 1,3× | 95,4 | 86,2 | 1,1× | 732 | 682 | 1,1× |
| `uc_filtre_metin` | 9,6 | 4,8 | 2,0× | 33,4 | 6,8 | 4,9× | 212 | 87,7 | 2,4× |
| `uc_ozet_sehir` | 10,8 | 10,4 | 1,0× | 65,6 | 77,5 | 0,8× | 583 | 546 | 1,1× |
| `uc_zaman_serisi` | 13,9 | 13,8 | 1,0× | 70,6 | 82,1 | 0,9× | 625 | 619 | 1,0× |

### Erişim biçimi — öncesi / sonrası

| senaryo | 10k | 100k | 1M |
|---|---|---|---|
| `satir_ilk_sayfa` | Index Scan → Index Scan | Bitmap Heap Scan → Bitmap Heap Scan | Seq Scan → Seq Scan |
| `satir_son_sayfa` | Index Scan → Index Scan | Bitmap Heap Scan → Bitmap Heap Scan | Seq Scan → Seq Scan |
| `filtre_metin` | Index Scan → Index Scan | Bitmap Heap Scan → Index Scan | Seq Scan → Bitmap Heap Scan |
| `filtre_metin_icinde` | Index Scan → Bitmap Heap Scan | Bitmap Heap Scan → Bitmap Heap Scan | Seq Scan → Seq Scan |
| `filtre_sayi` | Index Scan → Index Scan | Bitmap Heap Scan → Index Scan | Seq Scan → Index Scan |
| `filtre_donem` | Index Scan → Index Scan | Bitmap Heap Scan → Bitmap Heap Scan | Seq Scan → Seq Scan |
| `ozet_sehir` | Index Scan → Index Scan | Bitmap Heap Scan → Bitmap Heap Scan | Seq Scan → Seq Scan |
| `ozet_iki_anahtar` | Index Scan → Index Scan | Bitmap Heap Scan → Bitmap Heap Scan | Seq Scan → Seq Scan |
| `zaman_serisi_ay` | Index Scan → Index Scan | Bitmap Heap Scan → Bitmap Heap Scan | Seq Scan → Seq Scan |
| `medyan_sehir` | Index Scan → Index Scan | Bitmap Heap Scan → Bitmap Heap Scan | Seq Scan → Seq Scan |
| `farkli_musteri` | Index Scan → Index Scan | Bitmap Heap Scan → Bitmap Heap Scan | Seq Scan → Seq Scan |
| `ozet_filtreli` | Index Scan → Index Scan | Bitmap Heap Scan → Bitmap Heap Scan | Seq Scan → Seq Scan |
| `join_segment` | Index Scan → Index Scan | Bitmap Heap Scan + Index Scan → Bitmap Heap Scan + Index Scan | Seq Scan + Bitmap Heap Scan → Seq Scan + Index Scan |

### Yazma — indeksli faz

İndeksin bedeli okumada değil yazmada çıkar: her yeni satır bütün indekslere de işlenir. Yukarıdaki yazma tablosuyla karşılaştırılmalı.

| yol | satır | süre (sn) | satır/sn | not |
|---|---:|---:|---:|---|
| uç — 4 komşu set | 1.000 | 0,1 | 9.023 | 0,1 MB CSV |
| uç — komşusuz firma | 1.000 | 0,1 | 12.570 | 0,1 MB CSV |
| COPY (ikili akış) | 1.000 | 0,1 | 14.288 | doğrulama ve CSV ayrıştırma yok |
| uç — 4 komşu set | 10.000 | 0,4 | 28.239 | 0,8 MB CSV |
| uç — komşusuz firma | 10.000 | 0,3 | 34.174 | 0,8 MB CSV |
| COPY (ikili akış) | 10.000 | 0,8 | 12.204 | doğrulama ve CSV ayrıştırma yok |
| uç — 4 komşu set | 50.000 | 3,5 | 14.411 | 4,2 MB CSV |
| uç — komşusuz firma | 50.000 | 2,3 | 21.603 | 4,2 MB CSV |
| COPY (ikili akış) | 50.000 | 3,2 | 15.637 | doğrulama ve CSV ayrıştırma yok |

## Ayrıntı

| senaryo | nokta | ölçek | medyan | en kötü | dönen satır |
|---|---|---|---:|---:|---:|
| `satir_ilk_sayfa` | SQL | 10k | 10,1 | 10,3 | 25 |
| `satir_son_sayfa` | SQL | 10k | 10,3 | 10,9 | 25 |
| `filtre_metin` | SQL | 10k | 3,7 | 4,4 | 25 |
| `filtre_metin_icinde` | SQL | 10k | 6,4 | 7,2 | 25 |
| `filtre_sayi` | SQL | 10k | 3,1 | 3,5 | 25 |
| `filtre_donem` | SQL | 10k | 5,1 | 5,7 | 25 |
| `ozet_sehir` | SQL | 10k | 4,0 | 4,6 | 20 |
| `ozet_iki_anahtar` | SQL | 10k | 5,1 | 5,5 | 50 |
| `zaman_serisi_ay` | SQL | 10k | 7,9 | 9,6 | 37 |
| `medyan_sehir` | SQL | 10k | 7,4 | 8,0 | 20 |
| `farkli_musteri` | SQL | 10k | 12,0 | 13,4 | 1 |
| `ozet_filtreli` | SQL | 10k | 4,6 | 5,2 | 8 |
| `join_segment` | SQL | 10k | 21,2 | 22,7 | 4 |
| `uc_satir_ilk_sayfa` | uç | 10k | 13,6 | 14,4 | 25 |
| `uc_satir_son_sayfa` | uç | 10k | 16,5 | 18,6 | 25 |
| `uc_filtre_metin` | uç | 10k | 9,6 | 10,3 | 25 |
| `uc_ozet_sehir` | uç | 10k | 10,8 | 11,4 | 20 |
| `uc_zaman_serisi` | uç | 10k | 13,9 | 15,0 | 37 |
| `satir_ilk_sayfa` | SQL | 100k | 66,5 | 72,4 | 25 |
| `satir_son_sayfa` | SQL | 100k | 79,9 | 86,9 | 25 |
| `filtre_metin` | SQL | 100k | 35,2 | 37,4 | 25 |
| `filtre_metin_icinde` | SQL | 100k | 48,1 | 51,3 | 25 |
| `filtre_sayi` | SQL | 100k | 30,3 | 34,2 | 25 |
| `filtre_donem` | SQL | 100k | 37,7 | 40,9 | 25 |
| `ozet_sehir` | SQL | 100k | 64,4 | 73,3 | 20 |
| `ozet_iki_anahtar` | SQL | 100k | 84,9 | 98,6 | 50 |
| `zaman_serisi_ay` | SQL | 100k | 60,5 | 63,0 | 37 |
| `medyan_sehir` | SQL | 100k | 91,2 | 100 | 20 |
| `farkli_musteri` | SQL | 100k | 115 | 124 | 1 |
| `ozet_filtreli` | SQL | 100k | 40,8 | 48,6 | 8 |
| `join_segment` | SQL | 100k | 105 | 126 | 4 |
| `uc_satir_ilk_sayfa` | uç | 100k | 47,8 | 53,6 | 25 |
| `uc_satir_son_sayfa` | uç | 100k | 95,4 | 103 | 25 |
| `uc_filtre_metin` | uç | 100k | 33,4 | 46,2 | 25 |
| `uc_ozet_sehir` | uç | 100k | 65,6 | 74,8 | 20 |
| `uc_zaman_serisi` | uç | 100k | 70,6 | 75,4 | 37 |
| `satir_ilk_sayfa` | SQL | 1M | 493 | 526 | 25 |
| `satir_son_sayfa` | SQL | 1M | 665 | 711 | 25 |
| `filtre_metin` | SQL | 1M | 251 | 276 | 25 |
| `filtre_metin_icinde` | SQL | 1M | 337 | 365 | 25 |
| `filtre_sayi` | SQL | 1M | 210 | 227 | 25 |
| `filtre_donem` | SQL | 1M | 272 | 352 | 25 |
| `ozet_sehir` | SQL | 1M | 603 | 673 | 20 |
| `ozet_iki_anahtar` | SQL | 1M | 883 | 958 | 50 |
| `zaman_serisi_ay` | SQL | 1M | 651 | 705 | 37 |
| `medyan_sehir` | SQL | 1M | 1.167 | 1.271 | 20 |
| `farkli_musteri` | SQL | 1M | 1.061 | 1.193 | 1 |
| `ozet_filtreli` | SQL | 1M | 289 | 341 | 8 |
| `join_segment` | SQL | 1M | 519 | 581 | 4 |
| `uc_satir_ilk_sayfa` | uç | 1M | 335 | 375 | 25 |
| `uc_satir_son_sayfa` | uç | 1M | 732 | 788 | 25 |
| `uc_filtre_metin` | uç | 1M | 212 | 228 | 25 |
| `uc_ozet_sehir` | uç | 1M | 583 | 632 | 20 |
| `uc_zaman_serisi` | uç | 1M | 625 | 688 | 37 |

## Ayrıntı — indeksli faz

| senaryo | nokta | ölçek | medyan | en kötü | dönen satır |
|---|---|---|---:|---:|---:|
| `satir_ilk_sayfa` | SQL | 10k | 10,8 | 11,4 | 25 |
| `satir_son_sayfa` | SQL | 10k | 11,6 | 12,6 | 25 |
| `filtre_metin` | SQL | 10k | 1,6 | 1,9 | 25 |
| `filtre_metin_icinde` | SQL | 10k | 3,7 | 4,0 | 25 |
| `filtre_sayi` | SQL | 10k | 0,6 | 0,7 | 25 |
| `filtre_donem` | SQL | 10k | 5,6 | 6,2 | 25 |
| `ozet_sehir` | SQL | 10k | 4,8 | 5,3 | 20 |
| `ozet_iki_anahtar` | SQL | 10k | 5,9 | 6,4 | 50 |
| `zaman_serisi_ay` | SQL | 10k | 8,4 | 9,1 | 37 |
| `medyan_sehir` | SQL | 10k | 7,9 | 8,4 | 20 |
| `farkli_musteri` | SQL | 10k | 12,0 | 12,6 | 1 |
| `ozet_filtreli` | SQL | 10k | 5,1 | 5,4 | 8 |
| `join_segment` | SQL | 10k | 20,0 | 22,2 | 4 |
| `uc_satir_ilk_sayfa` | uç | 10k | 9,3 | 10,2 | 25 |
| `uc_satir_son_sayfa` | uç | 10k | 12,4 | 12,6 | 25 |
| `uc_filtre_metin` | uç | 10k | 4,8 | 7,3 | 25 |
| `uc_ozet_sehir` | uç | 10k | 10,4 | 11,3 | 20 |
| `uc_zaman_serisi` | uç | 10k | 13,8 | 14,4 | 37 |
| `satir_ilk_sayfa` | SQL | 100k | 65,8 | 71,0 | 25 |
| `satir_son_sayfa` | SQL | 100k | 88,9 | 91,7 | 25 |
| `filtre_metin` | SQL | 100k | 13,4 | 14,3 | 25 |
| `filtre_metin_icinde` | SQL | 100k | 31,0 | 32,8 | 25 |
| `filtre_sayi` | SQL | 100k | 0,6 | 0,8 | 25 |
| `filtre_donem` | SQL | 100k | 48,5 | 56,6 | 25 |
| `ozet_sehir` | SQL | 100k | 75,5 | 80,4 | 20 |
| `ozet_iki_anahtar` | SQL | 100k | 92,0 | 100 | 50 |
| `zaman_serisi_ay` | SQL | 100k | 63,7 | 66,3 | 37 |
| `medyan_sehir` | SQL | 100k | 110 | 122 | 20 |
| `farkli_musteri` | SQL | 100k | 119 | 122 | 1 |
| `ozet_filtreli` | SQL | 100k | 43,3 | 50,8 | 8 |
| `join_segment` | SQL | 100k | 120 | 124 | 4 |
| `uc_satir_ilk_sayfa` | uç | 100k | 47,5 | 51,7 | 25 |
| `uc_satir_son_sayfa` | uç | 100k | 86,2 | 88,4 | 25 |
| `uc_filtre_metin` | uç | 100k | 6,8 | 7,8 | 25 |
| `uc_ozet_sehir` | uç | 100k | 77,5 | 84,5 | 20 |
| `uc_zaman_serisi` | uç | 100k | 82,1 | 102 | 37 |
| `satir_ilk_sayfa` | SQL | 1M | 503 | 521 | 25 |
| `satir_son_sayfa` | SQL | 1M | 697 | 759 | 25 |
| `filtre_metin` | SQL | 1M | 111 | 113 | 25 |
| `filtre_metin_icinde` | SQL | 1M | 316 | 406 | 25 |
| `filtre_sayi` | SQL | 1M | 0,7 | 0,9 | 25 |
| `filtre_donem` | SQL | 1M | 310 | 324 | 25 |
| `ozet_sehir` | SQL | 1M | 556 | 705 | 20 |
| `ozet_iki_anahtar` | SQL | 1M | 926 | 1.013 | 50 |
| `zaman_serisi_ay` | SQL | 1M | 643 | 699 | 37 |
| `medyan_sehir` | SQL | 1M | 1.149 | 1.154 | 20 |
| `farkli_musteri` | SQL | 1M | 1.148 | 1.153 | 1 |
| `ozet_filtreli` | SQL | 1M | 306 | 383 | 8 |
| `join_segment` | SQL | 1M | 1.144 | 1.274 | 4 |
| `uc_satir_ilk_sayfa` | uç | 1M | 339 | 376 | 25 |
| `uc_satir_son_sayfa` | uç | 1M | 682 | 802 | 25 |
| `uc_filtre_metin` | uç | 1M | 87,7 | 97,3 | 25 |
| `uc_ozet_sehir` | uç | 1M | 546 | 755 | 20 |
| `uc_zaman_serisi` | uç | 1M | 619 | 715 | 37 |

