"""Görsel modelin belgeden alan çıkarımını ölçer.

`uret.py` belgeyi ve doğru cevabını birlikte üretti; bu araç modeli o küme
üzerinde koşturup cevabı doğrusuyla karşılaştırır.

İki kip var, ve aradaki fark bu adımın en önemli tasarım kararını sınıyor:

  serbest   modele yalnız "bilgileri çıkar" denir, alan adlarını kendi uydurur
  semali    hedef şema (alan adları + ne anlama geldikleri) modele verilir

İlk yoklamada serbest kip `firmasi_adi`, `kdv_tutarı`, `toplam_miktar` gibi her
belgede değişen adlar üretti; alıcıyı `satıcı_adresi` sandı. Bu çıktı bir veri
setine yazılamaz. NL→SQL adımındaki aynı ders: model serbest metin değil,
bizim verdiğimiz yapıyı doldurur.

Puanlama modelin metnine değil ÇÖZÜLMÜŞ değerine bakar: model "14.849,57" da
"14849.57" da döndürebilir, ikisi de aynı sayıdır. Bu hoşgörü şart, çünkü model
biçim kuralını sık ihlal ediyor — ama tam da bu yüzden sunucu tarafında
normalleştirme zorunlu, modelin biçimine güvenilmez.

Kullanım:
    python tools/belge_ureteci/olc.py --in data/belgeler --kip semali
    python tools/belge_ureteci/olc.py --in data/belgeler --kip serbest --limit 12
"""

import argparse
import base64
import io
import json
import re
import time
import unicodedata
import urllib.request
from collections import defaultdict
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

OLLAMA = "http://localhost:11434/api/generate"

# Şema, hedef veri setinin kolonlarının yerini tutuyor: gerçek akışta bu liste
# kullanıcının setinden gelecek. Şablona göre değişmesi kasıtlı — makbuza fatura
# şeması verilirse model olmayan alanı uydurmaya zorlanır, ölçüm de onu ölçer.
SEMALAR = {
    "fatura": {
        "alanlar": {
            "satici": "belgeyi düzenleyen firma adı",
            "alici": "belgenin kesildiği müşteri adı",
            "tarih": "belge tarihi",
            "belge_no": "fatura numarası",
            "vergi_no": "satıcının vergi numarası",
            "ara_toplam": "KDV hariç toplam (sayı)",
            "kdv_orani": "KDV yüzdesi (sayı)",
            "kdv": "KDV tutarı (sayı)",
            "genel_toplam": "KDV dahil toplam (sayı)",
        },
        "kalemler": {
            "urun": "ürün/hizmet adı",
            "birim": "ölçü birimi",
            "adet": "miktar (sayı)",
            "birim_fiyat": "birim fiyat (sayı)",
            "tutar": "satır tutarı (sayı)",
        },
    },
    "fis": {
        "alanlar": {
            "satici": "fişi veren işletme adı",
            "tarih": "fiş tarihi",
            "belge_no": "fiş numarası",
            "vergi_no": "işletmenin vergi numarası",
            "odeme": "ödeme şekli",
            "ara_toplam": "KDV hariç toplam (sayı)",
            "kdv_orani": "KDV yüzdesi (sayı)",
            "kdv": "KDV tutarı (sayı)",
            "genel_toplam": "ödenen toplam (sayı)",
        },
        "kalemler": {
            "urun": "ürün adı",
            "birim": "ölçü birimi",
            "adet": "miktar (sayı)",
            "birim_fiyat": "birim fiyat (sayı)",
            "tutar": "satır tutarı (sayı)",
        },
    },
    # Bu iki şablon bir kapsam düzeltmesinin ürünü: yüklenen belge fatura/fiş/makbuz olmak
    # zorunda değil, kâğıda yazılmış "Ahmet 100" da okunabilmeli. Şemaları da o yüzden
    # ticari belge sözlüğünden değil, notun kendi içeriğinden geliyor.
    "not": {
        "alanlar": {
            "baslik": "notun başlığı (yazılmamışsa null)",
            "tarih": "notta yazan tarih (yazılmamışsa null)",
        },
        "kalemler": {
            "kisi": "satırda yazan kişi adı",
            "tutar": "o kişinin yanında yazan sayı",
        },
    },
    "liste": {
        "alanlar": {"baslik": "listenin başlığı (yoksa null)"},
        "kalemler": {
            "kisi": "birinci sütun: kişi adı",
            "adet": "ikinci sütun: sayı",
            "tutar": "üçüncü sütun: tutar",
        },
    },
    "makbuz": {
        "alanlar": {
            "odenen": "ödemenin yapıldığı kişi/firma",
            "tarih": "belge tarihi",
            "belge_no": "belge numarası",
            "aciklama": "ödemenin açıklaması",
            "brut_tutar": "brüt tutar (sayı)",
            "stopaj_orani": "stopaj yüzdesi (sayı)",
            "stopaj": "stopaj tutarı (sayı)",
            "net_tutar": "net ödenen tutar (sayı)",
        },
        "kalemler": {},
    },
}

# Hangi alanın sayı, hangisinin tarih olduğu; karşılaştırma buna göre yapılır.
SAYISAL = {
    "ara_toplam", "kdv_orani", "kdv", "genel_toplam", "adet", "birim_fiyat",
    "tutar", "brut_tutar", "stopaj_orani", "stopaj", "net_tutar",
}
TARIHSEL = {"tarih"}


# ------------------------------ istem ------------------------------


def istem_semali(sablon: str, surum: str = "v1") -> str:
    sema = SEMALAR[sablon]
    kalem_bolum = (
        f"""
İSTENEN ALANLAR (kalem tablosu satırları):
{json.dumps(sema["kalemler"], ensure_ascii=False, indent=2)}
"""
        if sema["kalemler"]
        else """
Bu belge türünde kalem tablosu beklenmiyor; "kalemler" dizisini boş bırak.
"""
    )
    return f"""Bu bir Türkçe ticari belge görüntüsü. Aşağıda istenen alanlar var; yalnız onları çıkar.

İSTENEN ALANLAR (belge düzeyi):
{json.dumps(sema["alanlar"], ensure_ascii=False, indent=2)}
{kalem_bolum}
Kurallar:
- Alan adlarını AYNEN yukarıdaki gibi kullan, yeni alan ekleme, ad değiştirme.
- Belgede olmayan alanı null yaz; tahmin etme, uydurma.
- Sayıları ondalık nokta ile yaz: 1.500,75 -> 1500.75
- Tarihi YYYY-AA-GG biçiminde yaz.
- Belgede kalem tablosu yoksa "kalemler": [] yaz, kalem uydurma.
- Yalnız JSON döndür, açıklama yazma.{
    EK_KURALLAR_V2 if surum == "v2" else EK_KURALLAR_V3 if surum == "v3" else ""}

Biçim: {{"alanlar": {{...}}, "kalemler": [{{...}}]}}"""


# İkinci istem sürümü. Ölçümde iki açık kaldı ve ikisi de istemde karşılanabilir gibi
# duruyor; bu sürüm o varsayımı sınıyor:
#   1) `liste` şablonunda model satır ATLIYOR (4 satırdan 3 döndürüyor, ilk satırı da
#      kaydırıyor). Sayıyı önce kendisine saydırmak, atlamayı görünür kılabilir.
#   2) Belgede açıkça yazan tek kelimelik başlığı (`Kasa`) başlık olarak tanımıyor.
# Değişen yalnız bu iki madde; gerisi v1 ile birebir aynı tutuldu ki fark ölçülebilsin.
EK_KURALLAR_V2 = """
- Kalem tablosundaki satırları saymadan yazmaya başlama: kaç satır olduğunu belirle, sonra
  HEPSİNİ sırayla yaz. Hiçbir satırı atlama, satır birleştirme, sıra değiştirme.
- Belgenin en üstünde tek kelimelik bir yazı varsa (örneğin "Kasa", "Borçlar") bu belgenin
  başlığıdır; kolon adı sanıp atlama."""

# Üçüncü sürüm: v2 ÖLÇÜLDÜ ve `liste`de işi KÖTÜLEŞTİRDİ (satırların tamamı doğru: v1'de
# 13/24, v2'de 5/24). Sebebi anlaşıldı — "en üstteki tek kelimelik yazı başlıktır" maddesi
# modeli belgenin üstünü başlık saymaya daha çok itti, o da ilk VERİ satırını harcadı.
#
# Teşhis şu: başlık satırı olmayan bir tabloda model ilk satırı kolon adı sanıyor. Eksik
# isim her belgede birinci satır, başkası değil. v3 bu mekanizmayı doğrudan hedefliyor ve
# BAŞKA HİÇBİR ŞEY eklemiyor — v1'den tek maddelik fark, ki etkisi ölçülebilsin.
EK_KURALLAR_V3 = """
- Tabloda başlık satırı olmayabilir. Böyle bir tabloda EN ÜSTTEKİ satır da bir veri
  satırıdır; onu kolon adı sanıp atlama, kalemlerin arasına yaz."""

# Sayı denetimi: çıkarımdan AYRI, tek amaçlı küçük bir çağrı.
#
# Gerekçesi ölçülmüş bir kayıp: `liste` şablonunda model ilk veri satırını başlık sanıp
# harcıyor ve 24 belgenin 11'inde bir satır eksik geliyor. İstem ayarıyla kapatılamadı (v1
# 13/24, v2 5/24, v3 13/24). Eksik satır boş bir alan gibi göze çarpmadığı için sessizdir.
#
# Bu çağrının kapatıp kapatmadığı değil, KAPATABİLECEĞİ bile varsayım: model saymayı da
# çıkarımla aynı yanlış yapıyorsa denetim hiçbir şey söylemez. Ölçülen tam olarak bu.
ISTEM_SAYI = """Bu görüntüde bir tablo ya da liste var.

Kaç VERİ satırı olduğunu say. Kolon başlığı satırını sayma; yalnız içinde veri olan
satırları say.

Yalnız sayıyı yaz. Açıklama, birim, noktalama yazma."""

ISTEM_SERBEST = """Bu bir Türkçe ticari belge görüntüsü. İçindeki bilgileri JSON olarak çıkar.

Kurallar:
- Yalnız JSON döndür, açıklama yazma.
- Sayıları ondalık nokta ile yaz: 1.500,75 -> 1500.75
- Tarihi YYYY-AA-GG biçiminde yaz.
- Belgede kalem tablosu varsa her kalemi "kalemler" dizisine ekle; yoksa "kalemler": [].

Biçim: {"belge_turu": "...", "alanlar": {...}, "kalemler": [{...}]}"""


# ------------------------------ model çağrısı ------------------------------


def baslik_seridi_ekle(gorsel: Image.Image, kolonlar: list[str]) -> Image.Image:
    """Görüntünün üstüne kolon adlarından bir başlık şeridi ekler.

    Gerekçe ölçülmüş bir kayıp: başlık satırı olmayan tabloda model İLK VERİ SATIRINI kolon
    adı sanıp harcıyor (24 belgenin 11'inde bir satır eksik). İstem ayarı bunu kapatamadı
    (v1 13/24, v2 5/24, v3 13/24), satır sayısını ayrıca sormak da işe yaramadı (model saymayı
    çıkarımdan kötü yapıyor). Kalan fikir bu: harcayacağı başlığı kendimiz verelim ki veri
    satırına dokunmasın.

    Adlar üretimde hedef veri setinin kolonlarından gelir — yani bu hile ancak şema
    bilindiğinde uygulanabilir, keşif geçişinde uygulanamaz. Kolonların gerçek konumu
    bilinmediği için adlar genişliğe eşit aralıklarla dağıtılıyor; hizanın kaba olması
    modelin kolonları karıştırmasına yol açabilir, ölçüm bunu da gösterecek.
    """
    genislik = gorsel.width
    punto = max(14, genislik // 38)
    yazi = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", punto)
    yuksek = int(punto * 2.4)

    yeni = Image.new("RGB", (genislik, gorsel.height + yuksek), (255, 255, 255))
    cizim = ImageDraw.Draw(yeni)
    dilim = genislik / max(1, len(kolonlar))
    for sira, ad in enumerate(kolonlar):
        cizim.text((int(sira * dilim) + punto // 2, punto // 2), ad, font=yazi, fill=(20, 20, 20))
    cizim.line([0, yuksek - 3, genislik, yuksek - 3], fill=(110, 110, 110), width=2)

    yeni.paste(gorsel, (0, yuksek))
    return yeni


def gorsel_b64(yol: Path, kucult: int, kolonlar: list[str] | None = None) -> str:
    """Görüntüyü (gerekirse küçültüp) base64'e çevirir.

    Küçültme iki işe yarıyor: belirteç sayısı düşer (bağlam rahatlar, süre kısalır). Bedeli
    okunabilirlik — küçük puntolu fişte rakamlar bulanınca kalem hücreleri kaybediliyor.
    Sınırın nerede olduğu ölçülmeli, varsayılmamalı; bu seçenek o ölçüm için var.
    """
    if kucult <= 0 and not kolonlar:
        return base64.b64encode(yol.read_bytes()).decode()

    with Image.open(yol) as acilan:
        gorsel = acilan.convert("RGB")
        if kucult > 0:
            olcek = kucult / max(gorsel.size)
            if olcek < 1:
                gorsel = gorsel.resize(
                    (max(1, int(gorsel.width * olcek)), max(1, int(gorsel.height * olcek))),
                    Image.LANCZOS)
        if kolonlar:
            gorsel = baslik_seridi_ekle(gorsel, kolonlar)

        tampon = io.BytesIO()
        gorsel.save(tampon, format="JPEG", quality=90)

    return base64.b64encode(tampon.getvalue()).decode()


def cagir(yol: Path, istem: str, model: str, num_ctx: int, kucult: int = 0,
          kolonlar: list[str] | None = None) -> tuple[str, float, int]:
    b64 = gorsel_b64(yol, kucult, kolonlar)
    govde = json.dumps({
        "model": model,
        "prompt": istem,
        "images": [b64],
        "stream": False,
        # Bağlam açıkça veriliyor: tek sayfa belge görüntüsü ~3200 belirteç, Ollama'nın
        # 4096 varsayılanı yoğun belgede taşar ve taşma sessizdir — model belgenin
        # bir kısmını hiç görmediği hâlde kendinden emin cevap verir.
        "options": {"temperature": 0, "num_predict": 3072, "num_ctx": num_ctx},
    }).encode()
    bas = time.time()
    istek = urllib.request.Request(OLLAMA, data=govde, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(istek, timeout=1800) as yanit:
        sonuc = json.loads(yanit.read())
    return sonuc["response"], time.time() - bas, sonuc.get("prompt_eval_count", 0)


def sayi_sor(yol: Path, model: str, num_ctx: int, kucult: int) -> tuple[int | None, float]:
    """Belgedeki veri satırı sayısını tek başına sorar."""
    ham, sure, _ = cagir(yol, ISTEM_SAYI, model, num_ctx, kucult)
    eslesme = re.search(r"\d+", ham)
    return (int(eslesme.group()) if eslesme else None), sure


def json_coz(metin: str):
    """Model JSON'u kod bloğu içinde ya da açıklamayla sarılı döndürebiliyor."""
    temiz = re.sub(r"^```(?:json)?|```$", "", metin.strip(), flags=re.MULTILINE).strip()
    bas, son = temiz.find("{"), temiz.rfind("}")
    if bas < 0 or son <= bas:
        return None
    try:
        return json.loads(temiz[bas:son + 1])
    except json.JSONDecodeError:
        return None


# ------------------------------ değer çözme ------------------------------


def sayi_coz(deger):
    """Türkçe ve İngilizce biçimi birlikte kabul eder.

    Modelin döndürdüğü biçime güvenilemiyor: aynı yanıtta "721,83" ve "721.83"
    yan yana çıkıyor. Belirsiz olan tek durum tek ayraçlı sayı — "1.500" hem bin
    beş yüz hem bir buçuk okunabilir. Ayraçtan sonra tam üç hane varsa binlik
    sayılıyor; CSV tarafındaki tip algılama katmanı da aynı kuralı kullanıyor.
    """
    if deger is None:
        return None
    if isinstance(deger, (int, float)):
        return float(deger)

    metin = str(deger).strip()
    metin = re.sub(r"[^\d,.\-]", "", metin)
    if not metin or metin in {"-", ",", "."}:
        return None

    if "," in metin and "." in metin:
        # Sonda kalan ayraç ondalıktır: 14.849,57 / 14,849.57
        ondalik = "," if metin.rfind(",") > metin.rfind(".") else "."
        binlik = "." if ondalik == "," else ","
        metin = metin.replace(binlik, "").replace(ondalik, ".")
    elif "," in metin:
        metin = metin.replace(",", ".") if len(metin.split(",")[-1]) != 3 else metin.replace(",", "")
    elif "." in metin:
        if len(metin.split(".")[-1]) == 3 and metin.count(".") >= 1:
            metin = metin.replace(".", "")

    try:
        return float(metin)
    except ValueError:
        return None


def tarih_coz(deger):
    if deger is None:
        return None
    metin = str(deger).strip()
    for kalip, sira in [
        (r"^(\d{4})[-./](\d{1,2})[-./](\d{1,2})$", (0, 1, 2)),
        (r"^(\d{1,2})[-./](\d{1,2})[-./](\d{4})$", (2, 1, 0)),
    ]:
        eslesme = re.match(kalip, metin)
        if eslesme:
            parca = eslesme.groups()
            yil, ay, gun = (parca[i] for i in sira)
            return f"{int(yil):04d}-{int(ay):02d}-{int(gun):02d}"
    return metin


def metin_coz(deger):
    if deger is None:
        return None
    # Karşılaştırma noktalama ve büyük/küçük harfe takılmasın: "Ltd. Şti." ile
    # "Ltd Şti" arasındaki fark alan çıkarımı açısından hata değil.
    metin = unicodedata.normalize("NFC", str(deger))

    # Türkçe büyük harf tuzağı: Python'un casefold'u "İ" için "i" + birleşen nokta
    # (U+0307) üretir, yani "KREDİ KARTI" ile "Kredi Kartı" eşleşmez. Fiş şablonunda
    # metin büyük harfle basıldığı için bu, gerçekte olmayan 19 hata üretmişti.
    metin = metin.replace("İ", "i").replace("I", "ı").casefold()
    metin = re.sub(r"[^\w\s]", " ", metin)
    return re.sub(r"\s+", " ", metin).strip() or None


def esit(alan: str, beklenen, gelen) -> bool:
    if alan in SAYISAL:
        b, g = sayi_coz(beklenen), sayi_coz(gelen)
        return b is not None and g is not None and abs(b - g) < 0.01
    if alan in TARIHSEL:
        return tarih_coz(beklenen) == tarih_coz(gelen)
    return metin_coz(beklenen) == metin_coz(gelen)


# ------------------------------ puanlama ------------------------------


def duzlestir(cevap: dict) -> tuple[dict, list, bool]:
    """`alanlar` sarmalayıcısı atlanmış olabilir; iki yapıyı da kabul eder.

    Model istenen `{"alanlar": {...}}` yapısını sık sık düzleştirip alanları en
    üste koyuyor. Bunu hata saymak ölçümü değil sözleşmeyi ölçmek olurdu; ama
    sunucunun iki yapıyı da kabul etmesi gerektiği bilgisi kayda değer, o yüzden
    ayrıca raporlanıyor.
    """
    if not isinstance(cevap, dict):
        return {}, [], False

    kalemler = cevap.get("kalemler")

    if isinstance(cevap.get("alanlar"), dict):
        alanlar = dict(cevap["alanlar"])
        # Model kalem dizisini `alanlar`ın İÇİNE koyabiliyor. Kökte aramakla yetinmek,
        # belgeyi kusursuz okumuş bir yanıtı sıfır puanla geçmek demekti (liste_008: 18
        # hücrenin tamamı doğruydu, ayrıştırıcı görmedi).
        icteki = alanlar.pop("kalemler", None)
        if not isinstance(kalemler, list):
            kalemler = icteki
        duz = False
    else:
        alanlar = {a: d for a, d in cevap.items() if a not in {"kalemler", "belge_turu"}}
        duz = True

    if not isinstance(kalemler, list):
        kalemler = []
    return alanlar, kalemler, duz


def puanla(kayit: dict, cevap) -> dict:
    beklenen_alan = kayit["alanlar"]
    beklenen_kalem = kayit["kalemler"]

    if cevap is None:
        return {
            "gecerli": False, "alan_dogru": 0, "alan_toplam": len(beklenen_alan),
            "kalem_dogru": 0, "kalem_toplam": sum(len(k) for k in beklenen_kalem),
            "kalem_sayisi_dogru": False, "uydurma_kalem": False, "duz_yapi": False,
            "hatali_alanlar": sorted(beklenen_alan),
        }

    gelen_alan, gelen_kalem, duz = duzlestir(cevap)

    alan_dogru, hatali = 0, []
    for alan, beklenen in beklenen_alan.items():
        if esit(alan, beklenen, gelen_alan.get(alan)):
            alan_dogru += 1
        else:
            hatali.append(alan)

    # Kalemler belgedeki sırayla karşılaştırılıyor; sıra belgede görünür olduğu
    # için modelin onu koruması makul bir beklenti.
    hucre_dogru = 0
    hucre_toplam = sum(len(k) for k in beklenen_kalem)
    for beklenen, gelen in zip(beklenen_kalem, gelen_kalem):
        if not isinstance(gelen, dict):
            continue
        for alan, deger in beklenen.items():
            if esit(alan, deger, gelen.get(alan)):
                hucre_dogru += 1

    return {
        "gecerli": True,
        "alan_dogru": alan_dogru,
        "alan_toplam": len(beklenen_alan),
        "kalem_dogru": hucre_dogru,
        "kalem_toplam": hucre_toplam,
        "kalem_sayisi_dogru": len(gelen_kalem) == len(beklenen_kalem),
        "uydurma_kalem": not beklenen_kalem and bool(gelen_kalem),
        "duz_yapi": duz,
        "hatali_alanlar": hatali,
    }


# ------------------------------ akış ------------------------------


def yuzde(pay: int, toplam: int) -> str:
    return f"{100 * pay / toplam:5.1f}%" if toplam else "    —"


def main() -> None:
    a = argparse.ArgumentParser(description="Görsel modelin belge çıkarımını ölçer.")
    a.add_argument("--in", dest="girdi", default="data/belgeler")
    a.add_argument("--model", default="qwen2.5vl:7b")
    a.add_argument("--kip", choices=["semali", "serbest"], default="semali")
    a.add_argument("--limit", type=int, default=0, help="0 = tümü")
    a.add_argument("--sablon", default="", help="yalnız bu şablon")
    a.add_argument("--varyant", default="", help="temiz | foto")
    a.add_argument("--num-ctx", type=int, default=8192)
    a.add_argument("--kucult", type=int, default=0, help="uzun kenar sınırı (0 = küçültme yok)")
    a.add_argument("--istem", choices=["v1", "v2", "v3"], default="v1", help="istem sürümü")
    a.add_argument("--baslik-seridi", action="store_true",
                   help="görüntünün üstüne kolon adlarından başlık şeridi ekle")
    a.add_argument("--sayi-denetimi", action="store_true",
                   help="satır sayısını ayrı bir çağrıyla sorup çıkarımla karşılaştır")
    a.add_argument("--out", default="", help="ham yanıtların yazılacağı jsonl")
    # Puanlama kusuru bulunduğunda modeli yeniden koşturmak gereksiz (ve bir saat):
    # kaydedilmiş ham yanıtlar düzeltilmiş ölçütle yeniden puanlanır. NL→SQL adımındaki
    # `rescore` komutuyla aynı gerekçe.
    a.add_argument("--yeniden-puanla", default="", help="kaydedilmiş sonuç jsonl'ini yeniden puanla")
    s = a.parse_args()

    kok = Path(s.girdi)
    kayitlar = [json.loads(satir) for satir in (kok / "dogru.jsonl").read_text(encoding="utf-8").splitlines()]
    if s.sablon:
        kayitlar = [k for k in kayitlar if k["sablon"] == s.sablon]
    if s.varyant:
        kayitlar = [k for k in kayitlar if k["varyant"] == s.varyant]

    kaydedilmis = {}
    if s.yeniden_puanla:
        for satir in Path(s.yeniden_puanla).read_text(encoding="utf-8").splitlines():
            önceki = json.loads(satir)
            kaydedilmis[önceki["id"]] = önceki
        kayitlar = [k for k in kayitlar if k["id"] in kaydedilmis]

    if s.limit:
        kayitlar = kayitlar[: s.limit]

    cikti = open(s.out, "w", encoding="utf-8") if s.out and not kaydedilmis else None
    toplam = defaultdict(int)
    kirilim = defaultdict(lambda: defaultdict(int))
    sureler = []

    print(f"{len(kayitlar)} belge · model {s.model} · kip {s.kip} · istem {s.istem} · "
          f"bağlam {s.num_ctx} · küçültme {s.kucult or 'yok'}\n")

    for sira, kayit in enumerate(kayitlar, 1):
        if kaydedilmis:
            önceki = kaydedilmis[kayit["id"]]
            ham, sure, belirtec = önceki["ham"], önceki["sure"], önceki["belirtec"]
        else:
            istem = (ISTEM_SERBEST if s.kip == "serbest"
                     else istem_semali(kayit["sablon"], s.istem))
            kolonlar = (list(SEMALAR[kayit["sablon"]]["kalemler"])
                        if s.baslik_seridi else None)
            ham, sure, belirtec = cagir(
                kok / kayit["dosya"], istem, s.model, s.num_ctx, s.kucult, kolonlar)
        cevap = json_coz(ham)
        sonuc = puanla(kayit, cevap)
        sureler.append(sure)

        if s.sayi_denetimi and not kaydedilmis:
            beklenen_satir = len(kayit["kalemler"])
            okunan_satir = len(duzlestir(cevap)[1]) if cevap else 0
            sayilan, sayi_suresi = sayi_sor(kok / kayit["dosya"], s.model, s.num_ctx, s.kucult)
            sureler[-1] += sayi_suresi

            # Denetimin değeri, çıkarımın YANLIŞ olduğu belgelerde ne söylediğinde:
            #   yakaladi   çıkarım eksik/fazla, sayım doğruyu biliyor -> uyarı üretilebilir
            #   kacirdi    çıkarım eksik, sayım da aynı yanlışı yapıyor -> denetim işe yaramaz
            #   yanlis_alarm çıkarım doğru ama sayım farklı -> boşuna uyarı
            dogru_cikarim = okunan_satir == beklenen_satir
            dogru_sayim = sayilan == beklenen_satir
            toplam["sayim_dogru"] += int(dogru_sayim)
            if not dogru_cikarim and dogru_sayim:
                toplam["yakaladi"] += 1
            elif not dogru_cikarim and sayilan == okunan_satir:
                toplam["kacirdi"] += 1
            elif dogru_cikarim and sayilan != okunan_satir:
                toplam["yanlis_alarm"] += 1
            elif not dogru_cikarim:
                toplam["ikisi_de_baska"] += 1

            print(f"        satır: belgede {beklenen_satir}, çıkarım {okunan_satir}, "
                  f"sayım {sayilan}")

        anahtar = (kayit["sablon"], kayit["varyant"])
        for ad in ("alan_dogru", "alan_toplam", "kalem_dogru", "kalem_toplam"):
            toplam[ad] += sonuc[ad]
            kirilim[anahtar][ad] += sonuc[ad]
        for ad in ("gecerli", "kalem_sayisi_dogru", "uydurma_kalem", "duz_yapi"):
            toplam[ad] += int(sonuc[ad])
            kirilim[anahtar][ad] += int(sonuc[ad])
        toplam["belge"] += 1
        kirilim[anahtar]["belge"] += 1

        print(f"[{sira:3d}/{len(kayitlar)}] {kayit['id']:<24} "
              f"alan {sonuc['alan_dogru']}/{sonuc['alan_toplam']}  "
              f"kalem {sonuc['kalem_dogru']}/{sonuc['kalem_toplam']}  "
              f"{sure:5.1f} sn  {belirtec} belirteç"
              + ("  [KALEM UYDURDU]" if sonuc["uydurma_kalem"] else "")
              + ("  [JSON YOK]" if not sonuc["gecerli"] else ""))

        if cikti:
            cikti.write(json.dumps({"id": kayit["id"], "sure": sure, "belirtec": belirtec,
                                    "ham": ham, "puan": sonuc}, ensure_ascii=False) + "\n")

    if cikti:
        cikti.close()

    print(f"\n{'':<22}{'belge':>6}{'geçerli':>9}{'alan':>8}{'kalem':>8}{'satır sayısı':>14}")
    for (sablon, varyant), d in sorted(kirilim.items()):
        print(f"{sablon + ' / ' + varyant:<22}{d['belge']:>6}{yuzde(d['gecerli'], d['belge']):>9}"
              f"{yuzde(d['alan_dogru'], d['alan_toplam']):>8}"
              f"{yuzde(d['kalem_dogru'], d['kalem_toplam']):>8}"
              f"{yuzde(d['kalem_sayisi_dogru'], d['belge']):>14}")
    print(f"{'TÜMÜ':<22}{toplam['belge']:>6}{yuzde(toplam['gecerli'], toplam['belge']):>9}"
          f"{yuzde(toplam['alan_dogru'], toplam['alan_toplam']):>8}"
          f"{yuzde(toplam['kalem_dogru'], toplam['kalem_toplam']):>8}"
          f"{yuzde(toplam['kalem_sayisi_dogru'], toplam['belge']):>14}")

    if s.sayi_denetimi:
        print(f"\nsayı denetimi ({toplam['belge']} belge)")
        print(f"  sayım doğru                        : {toplam['sayim_dogru']}")
        print(f"  çıkarım yanlış, sayım YAKALADI     : {toplam['yakaladi']}")
        print(f"  çıkarım yanlış, sayım da kaçırdı   : {toplam['kacirdi']}")
        print(f"  çıkarım doğru, boşuna uyarı        : {toplam['yanlis_alarm']}")
        print(f"  ikisi de başka sayı söyledi        : {toplam['ikisi_de_baska']}")

    print(f"\nkalem uydurma (kalemsiz belgede kalem üretti): {toplam['uydurma_kalem']}/{toplam['belge']}")
    print(f"düz yapı (alanlar sarmalayıcısını atladı)     : {toplam['duz_yapi']}/{toplam['belge']}")
    print(f"belge başına süre: ort {sum(sureler) / len(sureler):.1f} sn · "
          f"en yüksek {max(sureler):.1f} sn · toplam {sum(sureler) / 60:.1f} dk")


if __name__ == "__main__":
    main()
