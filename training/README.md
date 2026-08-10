# Sorgu planlayıcı model eğitimi

Doğal dildeki Türkçe iş sorusunu JSON **sorgu planına** çeviren modelin ince ayarı.
Model SQL yazmaz; plan üretir, SQL'i doğrulanmış builder yazar (bkz. `KAPSAM.md`).

## Neden ince ayar

Bugün `qwen2.5-coder:7b` plan dilini her çağrıda istemin içinden okuyup öğreniyor
("bağlam içi öğrenme"). Bunun iki bedeli var:

1. **Yavaş** — istemin yarısı, modele işi anlatan 13 örnekten oluşuyor
2. **Kaygan** — kural istemde yazılı olsa da model bazen atlıyor

İnce ayardan sonra desen ağırlıklara gömülür: örnekler istemden çıkar (istem ~%40
kısalır) ve kurallara uyum artar.

## İstemin iki biçimi

`QueryPromptBuilder.Build(question, catalog, includeExamples)`

| Biçim | Kim kullanır | İçerik |
|---|---|---|
| Örnekli | Temel model (`qwen2.5-coder:7b`) | katalog + şema + değerler + kurallar + **13 örnek** |
| Örneksiz | İnce ayarlı model (`veriyonetim-*`) | katalog + şema + değerler + kurallar |

Ayrımı `OllamaOptions.FineTunedPrefix` yapar (varsayılan `veriyonetim`): model adı bu
önekle başlıyorsa örnekler gönderilmez. Kurallar iki biçimde de kalır — eğitim
beklenenden zayıf çıkarsa sistem yine de ayakta kalsın diye.

## Boru hattı

```
generate  →  paraphrase  →  build  →  [Kaggle: QLoRA]  →  ollama create  →  evaluate
```

Araç: `tools/VeriYonetim.TrainingData` (API projesine referans verir — istem ve
doğrulama tek kaynaktan gelir, kopyalanmış bir şema burada sessizce eskirdi).

### 1. `generate` — şablonlardan veri üret

```bash
dotnet run --project tools/VeriYonetim.TrainingData -- generate --out data
```

Soru → plan çevirisini modele öğreteceğiz, ama veriyi **ters yönde** üretmek çok daha
güvenli: önce geçerli bir plan kurulur, sonra o plandan Türkçe cümle yazılır.

Üretilen her çift **canlı yoldan** geçirilir (`PlanValidator`): `QueryPlanMapper` +
`DatasetRowQueryBuilder` / `DatasetAggregateQueryBuilder`. SQL metni üretilebiliyorsa
plan geçerlidir — kolon whitelist'i, tip uyumu, operatör denetimi, JOIN kurulabilirliği
hepsi orada işler. Veritabanına gidilmez; builder'lar saf.

Çıktı:

| Dosya | İçerik |
|---|---|
| `data/samples.train.jsonl` | eğitim örnekleri |
| `data/samples.eval.jsonl` | değerlendirme (`eval-seen` + `eval-unseen`) |

**Ayrılan kataloglar.** `Filo` ve `Kurs` katalogları eğitime **hiç girmez**
(`CatalogDef.HoldOut`). Gerçek kullanımda her firmanın kolon adları farklıdır; ezberi
ölçen bir doğruluk sayısı hiçbir şey söylemez. `eval-unseen` bu yüzden asıl ölçümdür.

### 2. `paraphrase` — dil çeşitliliği (isteğe bağlı, uzun sürer)

```bash
dotnet run --project tools/VeriYonetim.TrainingData -- paraphrase --out data --variants 2
```

Şablon cümleleri kalıplaşmış olur; gerçek kullanıcı aynı şeyi başka türlü sorar.
Her soru yerel modele yeniden yazdırılır, plan aynı kalır.

Sadakat denetimi iki katmanlı:

* özel adlar / sayılar / kodlar aynen korunmuş mu (`IsFaithful`)
* yeni cümle **aynı planla** hâlâ geçerli mi (`PlanValidator`)

İkincisi en sık şunu yakalıyor: modelin cümleye kendiliğinden yıl eklemesi — o zaman
plandaki tarih koşuluyla soru birbirini tutmaz.

Yarıda kesilirse kaldığı yerden devam eder.

### 3. `build` — eğitim biçimine çevir

```bash
dotnet run --project tools/VeriYonetim.TrainingData -- build --out data
```

`prompt` + `completion` üretir (`data/train.jsonl`, `data/eval.jsonl`). İstem burada
ekleniyor, üretim aşamasında değil: istem biçimi değişince bütün veriyi yeniden
üretmek gerekmesin diye.

### 4. Kaggle — QLoRA eğitimi

`training/kaggle_qlora.ipynb`

1. `data/train.jsonl` ve `data/eval.jsonl` dosyalarını Kaggle'a veri seti olarak yükle
2. Defteri aç, Accelerator = **GPU T4 x2**
3. `VERI_YOLU`nu kendi veri setine göre düzelt
4. Baştan sona çalıştır

Çıktı: `veriyonetim-planlayici.Q4_K_M.gguf` + `Modelfile`.

### 5. Ollama'ya kur

```bash
ollama create veriyonetim-planlayici:7b -f Modelfile
```

Ad **`veriyonetim` ile başlamalı** — sunucu bu önekten modelin ince ayarlı olduğunu
anlayıp isteme örnek koymuyor.

### 6. `evaluate` — ölç ve karşılaştır

```bash
# eğitim öncesi (baz çizgi)
dotnet run --project tools/VeriYonetim.TrainingData -- \
  evaluate --in data/samples.eval.jsonl --model qwen2.5-coder:7b

# eğitim sonrası
dotnet run --project tools/VeriYonetim.TrainingData -- \
  evaluate --in data/samples.eval.jsonl --model veriyonetim-planlayici:7b
```

İstem dosyadan okunmaz, her model için **kendi canlı biçimiyle** kurulur. Tek bir istem
dayatsaydık ya temel modeli haksız yere zayıflatır ya da ince ayarın hız kazancını
ölçemez hâle gelirdik.

Dört sayı raporlanır:

| Ölçüm | Anlamı |
|---|---|
| **Ayrıştı** | çıktı geçerli JSON mu |
| **Geçerli** | plan çalıştırılabilir mi (doğrulayıcıdan geçti mi) |
| **Doğru** | plan birebir aynı mı |
| **Sorgu** | gösterilecek kolonlar (`select`) hariç aynı mı |

Aradaki fark önemli: çalışan ama yanlış soruyu cevaplayan bir plan, hata verenden daha
tehlikelidir — kullanıcı yanlış sayıya inanır.

**Neden iki ayrı doğruluk?** Bazı sorular kolon adı geçirmiyor:

```
Soru   : "bugün girilen kayıtlar"
Şablon : select ["hat","fire"]
Model  : select ["vardiya","uretilen"]
```

İkisi de doğru — soru hangi kolonun gösterileceğini söylemiyor. Tam eşleşmeyi tek ölçüt
saymak, cevabı olmayan bir soruda modeli yanlış saymak olur. **Doğru** katı ölçüt,
**Sorgu** ise "sorgu mantığı doğru mu" sorusunun cevabı.

Karşılaştırma alan sırasına, `"1000"` / `1000` farkına ya da kolonun nitelikli
(`Satislar.adet`) veya sade (`adet`) yazılmasına takılmaz — hepsi
`PlanValidator.Canonical` içinde normalleştiriliyor.

Ölçüt değişirse `rescore` ile eski sonuç dosyası yeniden puanlanır — model tekrar
çalıştırılmaz (bir koşu ~20 dakika):

```bash
dotnet run --project tools/VeriYonetim.TrainingData -- \
  rescore --in data/samples.eval.qwen2-5-coder-7b.sonuc.jsonl
```

## Sonuçlar

300 soru (150 görülmüş şema + 150 görülmemiş şema).

| Model | İstem | Ayrıştı | Geçerli | **Doğru** | **Sorgu** | Tarih |
|---|---|---|---|---|---|---|
| `qwen2.5-coder:7b` (baz) | örnekli | %100 | %94,0 | **%45,7** | **%66,3** | 2026-08-06 |
| `veriyonetim-planlayici:7b` | örneksiz | %100 | **%100** | **%88,3** | **%100** | 2026-08-07 |

Bölümlere göre (Doğru / Sorgu):

| Model | Görülmüş şema | Görülmemiş şema |
|---|---|---|
| baz | %42,0 / %63,3 | %49,3 / %69,3 |
| ince ayarlı | %89,3 / **%100** | %87,3 / **%100** |

Baz çizginin okunuşu: model neredeyse her seferinde **çalışan** bir plan üretiyor
(%94,0), ama sorgu mantığı ancak üçte ikisinde doğru. Kalan üçte bir, sistemin en
tehlikeli hata türü: sorgu çalışır, grafik çizilir, kullanıcı yanlış sayıya bakar.
İnce ayarın hedefi bu boşluktu.

**Sonuç: boşluk kapandı.** Üç sayı birden anlamlı:

* **Geçerli %100** — çalıştırılamayan tek bir plan yok (bazda 18 tane vardı)
* **Sorgu %100** — 300 sorunun **hepsinde** sorgu mantığı doğru. "En çok hata veren
  tarifler" listesi bu koşuda **boş**; bazdaki 6 hata tipinin tamamı kapandı
* **Doğru %88,3** — kalan %11,7'nin tamamı `select` farkı, yani sorunun cevabını
  değiştirmeyen "hangi kolonlar gösterilsin" tercihi (bkz. iki ayrı doğruluk ölçütü)

**Ezber yok.** Görülmüş ve görülmemiş şema arasındaki fark yalnızca 2 puan (%89,3 →
%87,3), Sorgu ölçütünde hiç yok. `Filo` ve `Kurs` katalogları eğitime hiç girmedi; model
onları da aynı doğrulukla planlıyor, yani öğrendiği şey kolon adları değil plan dili.

**Ölçümün sınırı — dürüst okunuş.** Ayrılan boyut ŞEMA'dır, CÜMLE değil. Değerlendirme
soruları eğitim örnekleriyle aynı şablonlardan üretiliyor.

## Cümle dayanıklılığı — 2026-08-07

Yukarıdaki sayılar "kullanıcı cümleyi başka türlü kurunca ne oluyor" sorusunu hiç
ölçmüyordu. Ölçmek için değerlendirme kümesinin soruları yerel modele yeniden yazdırıldı
(`paraphrase --in data/samples.eval.jsonl`), planlar aynı bırakıldı. Aynı sorular şablon
cümleyle de puanlandı — küme farkı sonucu kirletmesin diye (kontrol grubu).

| Küme | Geçerli | Doğru | Sorgu |
|---|---|---|---|
| Kontrol (259 soru, şablon cümle) | %100 | %88,8 | **%100** |
| Parafraz (257 soru) — ham | %99,6 | %67,7 | **%79,0** |
| Parafraz — ispatlanabilir bozuk parafrazlar çıkarılınca | | | **%84,9** |
| Parafraz — 54 hatanın tamamı elle okununca | | | **~%96-98** |

**Ham sayı yanıltıcı.** 54 hatanın en az 18'i, parafrazın soruyu değiştirmesinden
kaynaklanıyor; elle okunduğunda oran çok daha yüksek çıkıyor. Model çoğu durumda
kendisine sorulan YENİ soruya doğru cevap veriyor, referans plan ise eski soruya ait:

```
ORİJİNAL : ödeme tipi alanında "Nak" GEÇEN kayıtlar   → contains
PARAFRAZ : Ödeme tipi 'Nak' OLAN kayıtları filtreleyin → eq
MODEL    : eq   ← yeni cümleye göre doğru, referansa göre yanlış
```

Gerçek model hatası 257 soruda ~4-9 tane. **O anki karar: ikinci bir eğitim koşusuna
gerek yok.** Bu karar aynı gün içinde geri alındı — gerekçesi aşağıda: elle okuyarak
çıkarılan ~%96-98 tahmini, kapı sertleştirilip ölçüm tekrarlandığında doğrulanmadı.

## Bilinen eksik — parafraz kapısı

Yukarıdaki ölçümün kirli olmasının sebebi, parafraz sadakat kapısının zayıf olması.
Bugün iki şey denetleniyor (`Program.cs`, `IsFaithful` + `PlanValidator`): özel
adlar/sayılar korunmuş mu, ve yeni cümle aynı planla hâlâ **kurulabilir** mi. İkincisi
güçlü görünüyor ama değil — kurulabilirlik, cümlenin o planı hâlâ ANLATTIĞINI test
etmiyor. "Veya"nın düşmesi, "ilk 5"in kaybolması, "geçen"in "olan"a dönmesi planı
kurulamaz yapmadığı için hepsi kapıdan geçiyor.

Aynı bozukluk **eğitim havuzunda da var**. Planın taşıdığı yapısal sinyal cümlede
hayatta kalmış mı diye bakıldığında:

```
Eğitimdeki parafraz : 5 839
  sinyal kaybetmiş  : 1 429  (%24,5)
    groupBy 706 | limit 562 | contains 103 | or 54 | having 38
```

Yani `data/samples.train.para.jsonl` **güvenilir değil**: dört örnekten biri modele
yanlış eşleme öğretir. Bugün "koşu 2 için hazır" durumda görünen 9 839 satırlık havuz bu
hâliyle kullanılmamalı.

**Bu koşuyu üreten model eski veriyle eğitildi**: 4 000 örnek, parafrazsız, 500 adım.

## Kapı sertleştirildi — 2026-08-07

Kapıya plan özelliği başına **sinyal denetimi** eklendi: plan `or` içeriyorsa cümlede
"veya/ya da", `limit:5` varsa "5" (yazıyla da olur), `contains` varsa "geçen/içeren",
`groupBy` varsa "göre/bazında", `having` varsa eşik ifadesi, `notIn`/`ne` varsa dışlama
sözcüğü bulunmalı. Yanına iki denetim daha kondu: planda karşılığı **olmayan** bir
yeteneği cümleye sokan parafraz ("kaç satış" → "kaç FARKLI marka"), ve dönem etiketinin
başka bir döneme kayması ("dün" → "Bugünün"). Sonuncusu anahtar kelime denetimlerinin
kaçırdığı en sinsi bozulmaydı: cümle kusursuz, plan kurulabilir, ama artık başka bir günü
sorguluyor. Ayrıca planın dokunmadığı bir veri seti adını cümleye ekleyen parafraz eleniyor.

| Küme | Önce | Sonra | Elenen |
|---|---|---|---|
| Eğitim havuzu | 9 839 | **8 452** | 1 387 |
| Değerlendirme parafrazı | 923 | **779** | 144 |

**Dayanıklılık temiz kümeyle yeniden ölçüldü** — ve elle okuyarak varılan ~%96-98 tahmini
tutmadı:

| Küme | Geçerli | Doğru | **Sorgu** |
|---|---|---|---|
| Parafraz — ham (257 soru) | %99,2 | %70,4 | %81,7 |
| **Parafraz — temiz kapıdan geçen (211 soru)** | %99,1 | %73,9 | **%87,7** |

Beklenti %95 üstüydü. Aradaki fark elle okumanın iyimserliğinden geliyor: bozuk parafrazlar
tek tek bakıldığında haklı görünüyor, ama toplu ve ölçütlü elendiğinde geriye kalan hataların
önemli kısmı gerçek model hatası çıkıyor. Şablon cümlede %100 olan Sorgu ölçütü, cümle
yeniden kurulduğunda %88'e düşüyor — yani model plan dilini öğrenmiş, **şablonun dışına
çıkan Türkçeyi** yeterince öğrenmemiş. Sebebi açık: koşu 1'in eğitim verisinde hiç parafraz
yoktu.

**Karar: koşu 2 başlatıldı.** Temizlenmiş 8 452 örnekli parafrazlı havuz, `max_steps = 400`.

Sorgunun kendisini yanlış kuran hatalar (baz, en sık 6'sı):

| Tarif | Hata |
|---|---|
| `group_top_n` | `sort: "sum(birim_fiyat)"` — gruplamalı sıralamada yalnız `key`/`value` geçerli |
| `agg_or` | Bozuk VEYA ağacı: tek çocuklu `or` + ayrı koşul → aslında VE oluyor |
| `time_compare` | `bucket`ı tarih olmayan kolona koyuyor |
| `agg_period` | Dönem etiketi yerine uydurma değer (`'now-30d'`) |
| `group_share` | `countDistinct` üzerinde yüzde istiyor — matematiksel olarak anlamsız |
| `time_bucket_period` | Olmayan kolon uyduruyor (`tarih`, oysa sette `ise_giris` var) |

Hepsi plan dilinin kurallarını bilmemekten kaynaklanıyor — istemde yazılı oldukları
hâlde. İnce ayarın kapatması gereken liste buydu; **ince ayarlı koşuda listenin tamamı
boş çıktı.** Kural istemde okunduğunda atlanabiliyor, ağırlığa geçtiğinde atlanmıyor —
ince ayarın bu işte ne işe yaradığının en somut kanıtı bu tablo.

## Eğitim koşusu 1 — 2026-08-06

| | |
|---|---|
| Süre | 435 dk (7 sa 15 dk), 52,2 sn/adım |
| Adım | 500 (4 000 örnek × 2 tur, etkin yığın 16) |
| Son kayıp | **0,0183** |
| Donanım | Kaggle T4 ×1 (ikinci kart kullanılmadı) |
| LoRA eklentisi | `rhymali/veriyonetim-planlayici-lora` (HF, özel) |

**Kayıp eğrisi — koşu 2'nin planını bu belirledi:**

| Adım | Kayıp | Yorum |
|---|---|---|
| 10 → 50 | 0,423 → 0,018 | Toplam düşüşün %96'sı |
| 50 → 100 | 0,018 → 0,008 | Kalan iyileşme |
| 100 → 250 | 0,008 → 0,004 | Küçük |
| **250 → 500** | 0,004 → 0,005 | **Hiçbir şey — gürültü** |

Yani 500 adımın ~400'ü (≈5,8 saat GPU) hiçbir şey öğretmedi. Koşu 2'de
`num_train_epochs` yerine **`max_steps = 400`** kullanılıyor: temizlenmiş 8 452 örnekli
parafrazlı havuzdan 6 400 **farklı** örnek görülür, süre 8,9 saat yerine ~5,8 saat olur.

**GGUF dönüşümü Kaggle'da patladı** — sebep disk değil, Unsloth'un llama.cpp kurucusunun
bozuk olması (`make clean` çağırıyor ama llama.cpp CMake'e geçmiş; ayrıca
`LLAMA_CURL is deprecated` uyarısını hata sayıyor). `training/kaggle_gguf.ipynb` aynı işi
elle yapıyor: doğru CMake bayrakları, ara dosyalar `/tmp`'ye (20 GB çıktı sınırını
aşmamak için), çıktıya yalnız 4,4 GB'lık `q4_k_m`.

**Dönüşüm bu defterle tamamlandı.** Çıktılar Hugging Face'te (özel):

| Depo | İçerik |
|---|---|
| `rhymali/veriyonetim-planlayici-lora` | LoRA eklentisi (~300 MB) |
| `rhymali/veriyonetim-planlayici-gguf` | `Q4_K_M` GGUF (4,36 GB) |

## Modeli kurarken: ad ÖNEMLİ

Ollama'ya doğrudan HF'den çekilirse model adı `hf.co/rhymali/...` olur ve
`OllamaOptions.FineTunedPrefix` (`veriyonetim`) ile eşleşmez. Sunucu o zaman modeli ince
ayarlı SAYMAZ ve isteme 13 few-shot örneği koyar — yani model eğitimde hiç görmediği bir
istem biçimiyle karşılaşır: hem yavaşlar hem doğruluk düşer. Belirti sessizdir, hata
verilmez.

Çekildikten sonra önekli bir ada kopyalanmalı (yer kaplamaz, katmanlar ortak):

```bash
ollama cp hf.co/rhymali/veriyonetim-planlayici-gguf:Q4_K_M veriyonetim-planlayici:7b
```

## Eğitim koşusu 2 — 2026-08-07 → 08

| | |
|---|---|
| Süre | 469 dk (7 sa 49 dk), **70,3 sn/adım** |
| Adım | 400 (`max_steps`; 400 × 16 = 6 400 örnek görüldü, havuzun ~2 000'i hiç görülmedi) |
| Son kayıp | **0,0306** |
| Veri | Parafraz sadakat kapısından geçmiş 8 452 örnek |
| LoRA eklentisi | `rhymali/veriyonetim-planlayici-lora-k2` |
| GGUF | `rhymali/veriyonetim-planlayici-gguf-k2`, Ollama'da `veriyonetim-planlayici:7b-k2` |

Adım başına süre koşu 1'e göre %35 arttı; ayarlarda sebebi yok, Kaggle donanım varyansı.
**52 sn/adım varsayımına güvenilmemeli.**

**Kayıp iki koşu arasında karşılaştırılamaz.** Koşu 1'de her örnek iki kez görüldü
(0,0183'ün bir kısmı ezber); koşu 2'de hiçbir örnek tekrar etmedi ve veri zorlaştı.
0,0306'nın daha yüksek olması beklenen ve daha dürüst bir sayı. Karar kayba değil yerel
ölçüme bakılarak verildi.

### Ölçüm

Kontrol kümesi (300 soru, yarısı eğitime hiç girmemiş `Filo` ve `Kurs` kataloglarından):

| Model | Geçerli | Doğru | Sorgu |
|---|---|---|---|
| `qwen2.5-coder:7b` (baz, örnekli istem) | %94,0 | %45,7 | %66,3 |
| `veriyonetim-planlayici:7b` (koşu 1) | %100 | %88,3 | %100 |
| **`veriyonetim-planlayici:7b-k2` (koşu 2)** | **%100** | **%89,7** | **%99,7** |

Kontrol kümesinde iki koşu aynı yerde; orada zaten tavan yakındı. Asıl soru parafraz
dayanıklılığıydı — koşu 2'nin varlık sebebi buydu:

| Model | Sorgu (779 parafraz sorusu) |
|---|---|
| koşu 1 | %87,7 |
| **koşu 2** | **%92,6** |

Görülmüş/görülmemiş şema farkı parafrazda 1,4 puan (%93,3 / %91,9) → **ezber yok.**

### 58 hatanın elle etiketlenmesi

Ölçüt "referans plana uy" diyor, ama referans plan **parafrazdan önceki** cümlenin planı;
parafraz cümleyi bozmuşsa model doğru davranıp yanlış puan alır. 58 kalemin tamamı tek tek
okundu (`training/kosu2_hata_etiketleri.md`):

| Etiket | Adet |
|---|---|
| Ölçüm kusuru (cümle planı anlatmıyor) | 31 |
| Belirsiz (iki okuma da savunulabilir) | 16 |
| **Gerçek model hatası** | **11** |

| Okuma | Sorgu |
|---|---|
| Ham ölçüm | %92,6 |
| **Ölçüm kusurları ayıklanınca** | **%96,5** |
| Belirsizler de modele yazılmazsa | %98,6 |

En sık ölçüm kusuru, parafrazın fiilin yönünü çevirmesi: "en **düşük** ilk 10" sorusunda
referans `desc` sıralıyor, model `asc` yapıp hata alıyor. Bir kalemde parafraz "önümüzdeki
yıl"ı "son bir yıl"a çevirmiş; referans hâlâ "gelecek tahmini yapılamıyor" diyor, model
geçmiş yılı doğru sorgulayıp hata almış. Üç kalem ise doğrudan puanlama kusuru: yalnız
`groupBy` **sırası** farklı, oysa SQL'de sonuç aynı.

Kalan 11 gerçek hatanın karakteri: ikisi sınır hatası ("500'den az" → `lte`, 500 dahil
edilmiş), biri arama metnini "düzeltmiş" (`'Servi'` → `Servis`) — üçü de **sessiz**, sorgu
çalışır ve sayı makul görünür. En ciddisi: "önümüzdeki çeyrekte" sorusunda reddetmesi
gerekirken dönem karşılaştırması üretmesi.

**Karar: koşu 3 yapılmadı.** Kaldıraç adım sayısı değil veri çeşitliliğiydi, ama kalan
açığın çoğu modelde değil ölçümde. Bozuk bir cetvele göre model ayarlamak, kazanç
görünümü üretir, kazanç üretmez. Koşu 3 ileride gerekirse başlangıç noktası
`kosu2_hata_etiketleri.md`'nin sonundaki dört madde.

### Ölçümden çıkan sunucu düzeltmesi

Gereksiz `join`: model, katalogda ilişkili iki set görünce sorunun dokunmadığı seti bağa
ekleyebiliyor. Bağ INNER JOIN kurulduğu için karşılığı olmayan satırlar düşer, birden çok
karşılığı olanlar çoğalır — sorgu hata vermez, **sayı sessizce bozulur**.

`TenantCatalog.DropUnusedDatasets` artık planın hiçbir kolon referansına sahip olmayan
`join` girdisini düşürüyor. `from`'a dokunulmuyor; plandaki kolon referansı hiç yoksa da
dokunulmuyor, çünkü `{from:A, join:[B], metrics:[count]}` planında bağın kendisi sorunun
anlamı olabilir. Düzeltme gizli değil: "şöyle anladım" özeti veri setlerini plandan değil
kapsamdan okuyor. 9 test (`UnusedJoinTests`).

Bu düzeltme **ölçüm sayılarını değiştirmez** — değerlendirme aracı planı `PlanValidator`
ile puanlar, sunucunun çalışma anındaki düzeltmesinden habersizdir ve öyle kalmalıdır:
orada ölçülen şey modelin ne ürettiğidir.
