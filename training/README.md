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
| `qwen2.5-coder:7b` (baz) | örnekli | %100 | %93,7 | **%45,7** | **%66,3** | 2026-08-06 |
| `veriyonetim-planlayici:7b` | örneksiz | — | — | — | — | — |

Bölümlere göre (baz): görülmüş şema %42,1 / %63,2 — görülmemiş şema %49,3 / %69,6.

Baz çizginin okunuşu: model neredeyse her seferinde **çalışan** bir plan üretiyor
(%93,7), ama sorgu mantığı ancak üçte ikisinde doğru. Kalan üçte bir, sistemin en
tehlikeli hata türü: sorgu çalışır, grafik çizilir, kullanıcı yanlış sayıya bakar.
İnce ayarın hedefi bu boşluk.

Görülmüş ve görülmemiş şemalar arasındaki fark temel modelde anlamsız — zaten ikisini de
ilk kez görüyor. Bu ayrım asıl ince ayardan SONRA anlam kazanacak: orada açılan fark
ezberin ölçüsü olacak.

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
hâlde. İnce ayarın kapatması gereken liste bu.

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
`num_train_epochs` yerine **`max_steps = 400`** kullanılacak: 9 839 örnekli parafrazlı
havuzdan 6 400 **farklı** örnek görülür, süre 8,9 saat yerine ~5,8 saat olur.

**GGUF dönüşümü Kaggle'da patladı** — sebep disk değil, Unsloth'un llama.cpp kurucusunun
bozuk olması (`make clean` çağırıyor ama llama.cpp CMake'e geçmiş; ayrıca
`LLAMA_CURL is deprecated` uyarısını hata sayıyor). `training/kaggle_gguf.ipynb` aynı işi
elle yapıyor: doğru CMake bayrakları, ara dosyalar `/tmp`'ye (20 GB çıktı sınırını
aşmamak için), çıktıya yalnız 4,4 GB'lık `q4_k_m`.
