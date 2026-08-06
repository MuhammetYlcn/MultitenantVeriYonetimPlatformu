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

Üç ayrı sayı raporlanır:

| Ölçüm | Anlamı |
|---|---|
| **Ayrıştı** | çıktı geçerli JSON mu |
| **Geçerli** | plan çalıştırılabilir mi (doğrulayıcıdan geçti mi) |
| **Doğru** | **aynı sorgu** mu (normalleştirilmiş karşılaştırma) |

Aradaki fark önemli: çalışan ama yanlış soruyu cevaplayan bir plan, hata verenden daha
tehlikelidir — kullanıcı yanlış sayıya inanır.

Karşılaştırma alan sırasına ya da `"1000"` / `1000` farkına takılmaz
(`PlanValidator.Canonical`); ölçtüğümüz şey modelin aynı sorguyu kurup kurmadığı.

Ölçüt değişirse `rescore` ile eski sonuç dosyası yeniden puanlanır — model tekrar
çalıştırılmaz (bir koşu ~20 dakika):

```bash
dotnet run --project tools/VeriYonetim.TrainingData -- \
  rescore --in data/samples.eval.qwen2-5-coder-7b.sonuc.jsonl
```

## Sonuçlar

300 soru (150 görülmüş şema + 150 görülmemiş şema).

| Model | İstem | Ayrıştı | Geçerli | **Doğru** | Tarih |
|---|---|---|---|---|---|
| `qwen2.5-coder:7b` (baz) | örnekli | %100 | %94,0 | **%45,7** | 2026-08-06 |
| `veriyonetim-planlayici:7b` | örneksiz | — | — | — | — |

Bölümlere göre: görülmüş şema %42,0 — görülmemiş şema %49,3.

Baz çizginin okunuşu: model neredeyse her seferinde **çalışan** bir plan üretiyor
(%94,0), ama bunların ancak yarısı **sorulan soruyu** cevaplıyor. Aradaki ~%48'lik
boşluk sistemin en tehlikeli hata türü: sorgu çalışır, grafik çizilir, kullanıcı yanlış
sayıya bakar. İnce ayarın hedefi bu boşluk.

Görülmüş ve görülmemiş şemalar arasındaki fark temel modelde anlamsız — zaten ikisini de
ilk kez görüyor (aradaki 7 puan 150'şer örneklik kümelerde gürültü sayılır). Bu ayrım
asıl ince ayardan SONRA anlam kazanacak: orada açılan fark ezberin ölçüsü olacak.

En sık hatalar (baz):

| Hata | Örnek |
|---|---|
| Gereksiz kolon döndürme | "ödeme tipi girilmemiş giderler" → bütün kolonlar |
| Özet yerine liste | "adet bakımından ilk 5 müşteri" → gruplamadan satır listesi |
| Bozuk VEYA ağacı | tek çocuklu `or` grubu + ayrı bir koşul → aslında VE |
| Uydurulmuş tarih | `inPeriod` yerine kafadan `2023-10-01` … `2023-12-31` |
| Yanlış işlem | "tipik üretilen adet" → `median` yerine `avg` |
| Olmayan kova | `bucket: "quarter"` |
