# Koşu 2 — parafraz ölçümündeki 58 hatanın elle etiketlenmesi

Ölçüm: `data/samples.eval.para.temiz.jsonl`, 779 soru, model `veriyonetim-planlayici:7b-k2`.
Sonuç: Geçerli %100, Doğru %80,9, **Sorgu %92,6** → 58 soruda üretilen plan referanstan
farklı bir sorgu kuruyor.

Bu belge o 58 kalemin ne olduğunu kaydeder. Gerekçesi şu: ölçüt "referans plana uy" diyor,
ama referans plan **parafrazdan önceki** cümlenin planı. Parafraz cümleyi bozmuşsa model
doğru davranıp yanlış puan alabilir. Bunu ayırmadan "model %92,6" demek, ölçüm kusurunu
modele yazmak olur.

Etiketleme el işidir ve yargı içerir; her kalemin gerekçesi aşağıda tek tek yazılı ki
karşı çıkılabilsin.

## Sonuç

| Etiket | Adet | Ne demek |
|---|---|---|
| **Ö — ölçüm kusuru** | 31 | Cümle planı artık anlatmıyor; modelin cevabı cümleye uygun ya da referans kadar savunulabilir |
| **B — belirsiz** | 16 | Cümle iki türlü okunabiliyor; ikisi de savunulabilir |
| **M — model hatası** | 11 | Cümle yeterince açık, model yanlış okumuş |

Buradan çıkan aralık:

| Okuma | Sorgu |
|---|---|
| Ham ölçüm (58'i de modele yaz) | %92,6 |
| **Açık ölçüm kusurları ayıklanınca (Ö)** | **%96,5** |
| Belirsizler de modele yazılmazsa (Ö+B) | %98,6 |

Dürüst manşet sayı **%96,5**: belirsizleri modelin lehine saymak için sebep yok, ama
cümlenin planı anlatmadığı 31 kalemi modele yazmak için de yok.

## M — model hatası (11)

| # | Soru | Hata |
|---|---|---|
| 16 | "…önümüzdeki çeyrekte ürettiği adetlere göre…" | **Gelecek tahminini reddetmedi**, dönem karşılaştırması üretti |
| 10 | "Tutarı 2500 TL ve üzerinde olmayan" | Sınır: `lt` yerine `lte` (2500 dahil edildi) |
| 18 | "Miktarı 500'den az olan" | Sınır: `lt` yerine `lte` |
| 57 | "Departmanda 'Servi' geçen" | Arama metnini **düzeltti**: `Servi` → `Servis` |
| 4 | "Maliyetlerin en yüksek olduğu ilk 5 fatura" | `tutar` yerine `kdv` ile sıraladı |
| 26 | "Kursun sonuna kadar gelmeyen katılım kayıtları" | `kurs` yerine ilgisiz `sehir` kolonunu boş aradı |
| 56 | "KS-2 numarasına sahip **kursiyerin** ücreti" | `kursiyer_kodu` yerine `kayit_no` ile eşleştirdi |
| 15 | "…fiyat farkı en az olan ilk 5 siparişin taşıma şirketleri" | Satır listesi yerine gruplama kurdu |
| 33 | "Kilometre **başına** yakıt tüketimi…" | Plan dilinde oran yok; reddetmek yerine olmayan bir gruplama uydurdu |
| 52 | "…**katılımlarını** sorgulayın ve İleri seviyeye ait olanları sayın" | Katılımları değil kursiyerleri saydı |
| 50 | "kursu Excel ve Muhasebe dışında olan kayıtların adedi" | Gereksiz `join` ekledi → INNER JOIN sayıyı sessizce bozar |

50 numaralı hata sunucu tarafında kapatıldı: planın hiçbir kolon referansına sahip olmayan
`join` girdisi artık düşürülüyor (bkz. `TenantCatalog.DropUnusedDatasets`, `UnusedJoinTests`).

Kalanların ortak yanı: 10, 18 ve 57 **sessiz** hatalar — sorgu çalışır, sayı makul görünür,
kimse fark etmez. 16 ise en ciddisi: sistemin "bunu yapamam" demesi gereken yerde sayı
üretmesi.

## Ö — ölçüm kusuru (31)

### Parafraz fiili/yönü değiştirmiş, model cümleyi doğru okumuş (12)

| # | Soru | Referans | Model | Cümleye göre |
|---|---|---|---|---|
| 23 | "En yüksek **ücretli** ilk beş iş emri" | `sure_dk` sıralar | `ucret` sıralar | model |
| 24 | "**En az** süredeki ilk beş" | `desc` | `asc` | model |
| 44 | "Ücreti **en düşük** ilk 10 kurs" | `desc` | `asc` | model |
| 27 | "**Kaç farklı marka** araç var" | `count` | `countDistinct(marka)` | model |
| 37 | "Kilometre ve yakıt tüketimini **toplayalım**" | `count` | `sum(km), sum(yakit_lt)` | model |
| 12 | "Maaşları **toplayıp** yüzde oranı" | `count` | `sum(maas)` | model |
| 39 | "Kursları **içeren** kayıtlar" | `isNull` | `notNull` | model |
| 35 | "En yüksek kilometreyi **ne kadar**" | satır (kim) | `max(km)` (ne kadar) | model |
| 40 | "**Ne kadar kişi** katıldı" | satır listesi | `count` | model |
| 36 | "**Departmanlara göre** araç dağılımı" | gruplamasız | `groupBy: departman` | model |
| 42 | "**Departmanlara göre** dağılım ve toplam" | gruplamasız | `groupBy: departman` | model |
| 55 | "**Son bir yılın** toplam kilometresi" | `unsupported` (gelecek) | geçmiş yıl filtresi | model |

55 özellikle dikkat çekici: parafraz geleceği geçmişe çevirmiş, referans hâlâ "gelecek
tahmini yapılamaz" diyor. Model doğru cevabı verip hata almış.

### Parafraz planda olmayan bir şey eklemiş (5)

| # | Soru | Not |
|---|---|---|
| 3 | "**Departman ve** kalem bazında" | Cümle ikinci kırılımı eklemiş, model uymuş |
| 17 | "…olduğu **durumları** listeleyin" | "durum" şemada kolon adı; model ona göre gruplamış |
| 29 | "**Kursiyer kartları ile birlikte** şehir bazında" | Cümle bağı istiyor, referansta bağ yok |
| 2 | "Kaç farklı şehirin **ürün alışverişleri**" | Cümle satış verisine işaret ediyor, model o setten kurmuş |
| 5 | Parti/hat/vardiya karışık cümle | Dönem karşılaştırması cümleden düşmüş |

### Parafraz şemada olmayan sözcük getirmiş (4)

20, 48, 49 ("güncelleme" diye bir kolon yok — 49'da model "bu bilgi yok" diyor, savunulabilir),
1 ("mağaza" şemada yok).

### Referansın kendi tercihi cümlede yok (7)

22 (hangi tarih kolonu), 21, 25, 47, 58, 54 (bozuk "Seneyin"), 41 (cümle açıkça
"kayıt no'su" diyor, referans `kursiyer_kodu` filtreliyor).

### Ölçütün kendi kusuru (3)

28, 45, 51 — yalnız `groupBy` **sırası** farklı. SQL'de `GROUP BY a, b` ile `GROUP BY b, a`
aynı sonucu verir; fark yalnız kolon sırasında. Karşılaştırma sırayı önemsediği için hata
yazılmış. Bu üç kalem parafrazla bile ilgili değil, doğrudan puanlama kusuru.

## B — belirsiz (16)

6, 7, 8, 9 (dördü de "paket **kırılımı**" ifadesinin "kırılım süresi" diye okunabilmesi —
model gruplamayı düşürmüş), 11, 13, 14, 19, 30, 31, 32, 34, 38, 43, 46, 53.

Ortak yanları: parafraz cümleyi bozmamış ama iki okumaya açık hale getirmiş; "topla"nın
hem listelemek hem toplamak anlamına gelmesi, "kayıtları hesapla"nın sayım mı listeleme mi
olduğu gibi.

## Bundan çıkan iş maddeleri

1. **Parafraz sadakat kapısının kör noktası.** Kapı, planın özelliklerinin cümlede
   karşılığı olup olmadığına bakıyor; ama cümlenin **fiili değiştirmesini** (`say`→`topla`,
   `en yüksek`→`en düşük`, `boş olan`→`dolu olan`) ve plana **olmayan bir kırılım
   eklemesini** yakalamıyor. 17 kalem buradan geliyor — havuzun tamamına oranlarsak
   değerlendirme kümesinin ~%2'si hatalı.
2. **`groupBy` sırası puanlamada önemsenmemeli.** Üç kalem yalnız bu yüzden hata.
3. **Sınır hataları** (`lt`/`lte`) eğitim verisinde az temsil ediliyor olabilir: "…den az",
   "…ve üzerinde olmayan" kalıpları için ayrı örnek üretmeye değer.
4. **Oran soruları** ("kilometre başına yakıt") plan dilinde yok. Model uyduruyor;
   `unsupported` ile reddetmeyi öğrenmesi gerekir.
