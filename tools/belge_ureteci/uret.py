"""Belge/fatura OCR adımı için sentetik ölçüm kümesi üreteci.

Elimizde gerçek fatura fotoğrafı yok; modelin alan çıkarımını ölçebilmek için
belgeyi ve doğru cevabını birlikte üretiyoruz. Doğru cevap elle etiketlenmiş
değil, belgeyi çizen kodun kendisinden geliyor — dolayısıyla etiket hatası yok.

Her belge İKİ varyant olarak yazılıyor:

  temiz   doğrudan çizilmiş görüntü (dijital PDF / iyi tarama karşılığı)
  foto    aynı belge; eğrilik, perspektif, gölge, bulanıklık, gürültü ve JPEG
          bozulması eklenmiş (telefonla çekilmiş belge karşılığı)

İkisi aynı belgeden geldiği için aradaki puan farkı doğrudan "fotoğraf
koşulunun bedeli" olarak okunabilir. Tek varyantla ölçmek bu ayrımı kaybettirir:
model zayıf çıktığında sebebin çıkarım mı yoksa görüntü kalitesi mi olduğu
bilinemez.

Şablonlar bilerek üç farklı yapıda:

  fatura  kalem tablosu + belge düzeyi alanlar (çok satırlı okuma)
  fis     dar, sabit genişlikli, sıkışık kalem listesi (tablo çizgisi yok)
  makbuz  kalem tablosu HİÇ yok, yalnız belge düzeyi alanlar (tek satırlık okuma)

`makbuz`'un varlık sebebi: "kaç satır yazılacağına model karar verir" kararı
ancak kalemsiz belgede de doğru davrandığı ölçülürse savunulabilir.

Kullanım:
    python tools/belge_ureteci/uret.py --out data/belgeler --adet 30 --seed 42
"""

import argparse
import json
import math
import random
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

FONT_DIZIN = Path("C:/Windows/Fonts")

# Şablon başına birden çok yazı tipi: tek tipe bağlı kalmak modelin o tipe
# alışıp alışmadığını ölçmeyi imkânsız kılar.
YAZI_TIPLERI = {
    "duz": ["arial.ttf", "segoeui.ttf", "calibri.ttf"],
    "kalin": ["arialbd.ttf", "segoeuib.ttf", "calibrib.ttf"],
    "serif": ["times.ttf", "timesbd.ttf"],
    "sabit": ["consola.ttf", "cour.ttf"],
}

FIRMALAR = [
    "Yılmaz Elektrik Malzemeleri Ltd. Şti.",
    "Öztürk Gıda Sanayi A.Ş.",
    "Çağdaş İnşaat Taahhüt Ltd. Şti.",
    "Şahin Otomotiv Yedek Parça",
    "Güneş Tekstil ve Konfeksiyon A.Ş.",
    "Demirbaş Hırdavat San. Tic.",
    "Akın Bilişim Teknolojileri",
    "Köroğlu Nakliyat ve Lojistik",
    "Üçler Kırtasiye ve Ofis Ürünleri",
    "Bereket Un ve Yem Sanayi",
]

ALICILAR = [
    "Anadolu Yapı Market",
    "Ege Toptan Gıda",
    "Marmara Teknik Servis",
    "Toros Mobilya İmalat",
    "Fırat Tarım Ürünleri",
]

SEHIRLER = ["İstanbul", "Ankara", "İzmir", "Bursa", "Gaziantep", "Kayseri", "Şanlıurfa", "Çorum"]

URUNLER = [
    ("Bakır kablo 3x2,5 mm", "metre"),
    ("Priz topraklı beyaz", "adet"),
    ("Anahtar çift yollu", "adet"),
    ("Sigorta 16A otomatik", "adet"),
    ("Buzdolabı contası", "adet"),
    ("Ayçiçek yağı 5 L", "teneke"),
    ("Toz şeker 50 kg", "çuval"),
    ("Zeytinyağı sızma 1 L", "şişe"),
    ("Çelik vida 4x40", "kutu"),
    ("Matkap ucu seti", "takım"),
    ("A4 fotokopi kâğıdı", "paket"),
    ("Toner kartuş siyah", "adet"),
    ("Fren balatası ön", "takım"),
    ("Motor yağı 10W40", "litre"),
    ("Pamuklu kumaş ham", "metre"),
    ("Düğme sedef 12 mm", "gros"),
    ("Yem mısır kırığı", "ton"),
    ("Çimento torba 50 kg", "torba"),
    ("Alçı sıva ince", "torba"),
    ("Silikon şeffaf", "tüp"),
]

ODEME = ["Nakit", "Kredi Kartı", "Havale/EFT", "Çek"]

KISILER = [
    "Ahmet", "Mehmet Yılmaz", "Ayşe", "Fatma Öztürk", "Hüseyin", "Zeynep Şahin",
    "Mustafa", "Elif", "İbrahim Çelik", "Hatice", "Ömer", "Şule Kaya",
]

NOT_BASLIKLARI = ["Kasa", "Borçlar", "Ödemeler", "Alacak", "Avans", "Yol parası"]

# El yazısı benzeri yazı tipleri. Gerçek el yazısı değil — bunu iddia etmiyoruz; amaç
# "matbu olmayan, düzensiz" metnin modele ne yaptığını görmek. Gerçek ölçüm için elle
# yazılmış birkaç kâğıdın fotoğrafı gerekir.
EL_YAZISI = ["Inkfree.ttf", "segoesc.ttf", "comic.ttf", "MTCORSVA.ttf"]


# ------------------------------ yardımcılar ------------------------------


def tl(deger: float) -> str:
    """Türkçe para biçimi: 1.500,75

    Bu biçim ölçümün asıl noktalarından biri. Modelin `1.500,75`'i bin beş yüz
    olarak okuması gerekiyor; nokta ondalık ayracı sanırsa 1,5 yazar ve sayı
    sessizce bozulur (CSV tarafında aynı hata `69cbe7d`'de düzeltildi).
    """
    tam = f"{deger:,.2f}"
    return tam.replace(",", "\x00").replace(".", ",").replace("\x00", ".")


def buyut(metin: str) -> str:
    """Türkçe büyük harf: i → İ, ı → I.

    Python'un `.upper()`'ı İngilizce kuralı uygular ve "Sanayi"yi "SANAYI" yapar. Gerçek
    hiçbir fiş böyle basmaz; belgeye bunu yazmak, modeli doğru Türkçe yazdığı için hatalı
    saymak demekti (ilk ölçümde fişteki 18/24 `satici` hatasının sebebi buydu).
    """
    return metin.replace("i", "İ").replace("ı", "I").upper()


def font(tur: str, boyut: int, rastgele: random.Random) -> ImageFont.FreeTypeFont:
    ad = rastgele.choice(YAZI_TIPLERI[tur])
    return ImageFont.truetype(str(FONT_DIZIN / ad), boyut)


def sabit_font(ad: str, boyut: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(FONT_DIZIN / ad), boyut)


def genislik(cizim: ImageDraw.ImageDraw, metin: str, f: ImageFont.FreeTypeFont) -> int:
    kutu = cizim.textbbox((0, 0), metin, font=f)
    return kutu[2] - kutu[0]


def saga_yaz(cizim, sag_x, y, metin, f, renk=(20, 20, 20)):
    cizim.text((sag_x - genislik(cizim, metin, f), y), metin, font=f, fill=renk)


def ortala_yaz(cizim, orta_x, y, metin, f, renk=(20, 20, 20)):
    cizim.text((orta_x - genislik(cizim, metin, f) // 2, y), metin, font=f, fill=renk)


def kimlik(sablon: str, sira: int) -> str:
    return f"{sablon}_{sira:03d}"


# ------------------------------ içerik üretimi ------------------------------


def kalemler_uret(rastgele: random.Random, en_az: int, en_cok: int) -> list[dict]:
    secilen = rastgele.sample(URUNLER, rastgele.randint(en_az, en_cok))
    kalemler = []
    for urun, birim in secilen:
        adet = rastgele.choice([1, 2, 3, 4, 5, 8, 10, 12, 20, 25, 40, 100])
        fiyat = round(rastgele.uniform(4, 950), 2)
        kalemler.append(
            {
                "urun": urun,
                "birim": birim,
                "adet": adet,
                "birim_fiyat": fiyat,
                "tutar": round(adet * fiyat, 2),
            }
        )
    return kalemler


def toplamlar(kalemler: list[dict], kdv_orani: int) -> dict:
    ara = round(sum(k["tutar"] for k in kalemler), 2)
    kdv = round(ara * kdv_orani / 100, 2)
    return {"ara_toplam": ara, "kdv_orani": kdv_orani, "kdv": kdv, "genel_toplam": round(ara + kdv, 2)}


def tarih_uret(rastgele: random.Random) -> tuple[str, str]:
    yil = rastgele.choice([2025, 2026])
    ay = rastgele.randint(1, 12)
    gun = rastgele.randint(1, 28)
    return f"{gun:02d}.{ay:02d}.{yil}", f"{yil}-{ay:02d}-{gun:02d}"


# ------------------------------ şablon: fatura ------------------------------


def ciz_fatura(rastgele: random.Random) -> tuple[Image.Image, dict]:
    """A4 (150 dpi) fatura: başlık bloğu, kalem tablosu, toplam bloğu."""
    G, Y = 1240, 1754
    gorsel = Image.new("RGB", (G, Y), (255, 255, 255))
    cizim = ImageDraw.Draw(gorsel)

    serif = rastgele.random() < 0.35
    duz = "serif" if serif else "duz"
    kalin = "serif" if serif else "kalin"

    f_baslik = font(kalin, 40, rastgele)
    f_orta = font(duz, 26, rastgele)
    f_kucuk = font(duz, 22, rastgele)
    f_tablo = font(duz, 23, rastgele)
    f_tablo_bas = font(kalin, 23, rastgele)

    satici = rastgele.choice(FIRMALAR)
    alici = rastgele.choice(ALICILAR)
    sehir = rastgele.choice(SEHIRLER)
    tarih_metin, tarih_iso = tarih_uret(rastgele)
    belge_no = f"{rastgele.choice(['A', 'B', 'FTR'])}{rastgele.randint(100000, 999999)}"
    vergi_no = str(rastgele.randint(1000000000, 9999999999))

    kalemler = kalemler_uret(rastgele, 3, 9)
    top = toplamlar(kalemler, rastgele.choice([1, 10, 20, 20, 20]))

    kenar = rastgele.choice([70, 90, 110])
    y = kenar

    # Kimi faturada logo yerine kutu, kimisinde yok: yerleşim tek kalıba
    # oturmasın, model konumdan değil metinden okumayı öğrensin.
    if rastgele.random() < 0.5:
        cizim.rectangle([kenar, y, kenar + 120, y + 90], outline=(150, 150, 150), width=2)
        cizim.text((kenar + 24, y + 32), "LOGO", font=f_kucuk, fill=(150, 150, 150))
        metin_x = kenar + 150
    else:
        metin_x = kenar

    cizim.text((metin_x, y), satici, font=f_baslik if len(satici) < 32 else f_orta, fill=(15, 15, 15))
    cizim.text((metin_x, y + 52), f"{sehir} · Tel: 0{rastgele.randint(200, 555)} {rastgele.randint(1000000, 9999999)}",
               font=f_kucuk, fill=(90, 90, 90))
    cizim.text((metin_x, y + 80), f"Vergi No: {vergi_no}", font=f_kucuk, fill=(90, 90, 90))

    y += 150
    ortala_yaz(cizim, G // 2, y, "FATURA", font(kalin, 34, rastgele))
    y += 60

    cizim.text((kenar, y), f"Sayın: {alici}", font=f_orta, fill=(20, 20, 20))
    saga_yaz(cizim, G - kenar, y, f"Tarih: {tarih_metin}", f_orta)
    y += 36
    saga_yaz(cizim, G - kenar, y, f"Fatura No: {belge_no}", f_orta)
    y += 60

    # --- kalem tablosu ---
    kolonlar = [
        ("Ürün / Hizmet", kenar, "sol"),
        ("Birim", kenar + 520, "sol"),
        ("Miktar", kenar + 680, "sag"),
        ("Birim Fiyat", kenar + 880, "sag"),
        ("Tutar", G - kenar, "sag"),
    ]
    izgara = rastgele.random() < 0.6
    satir_yuksek = 40

    cizim.rectangle([kenar - 10, y - 8, G - kenar + 10, y + satir_yuksek - 8], fill=(238, 238, 238))
    for ad, x, hiza in kolonlar:
        if hiza == "sol":
            cizim.text((x, y), ad, font=f_tablo_bas, fill=(30, 30, 30))
        else:
            saga_yaz(cizim, x, y, ad, f_tablo_bas)
    y += satir_yuksek

    for kalem in kalemler:
        cizim.text((kolonlar[0][1], y), kalem["urun"], font=f_tablo, fill=(25, 25, 25))
        cizim.text((kolonlar[1][1], y), kalem["birim"], font=f_tablo, fill=(25, 25, 25))
        saga_yaz(cizim, kolonlar[2][1], y, str(kalem["adet"]), f_tablo)
        saga_yaz(cizim, kolonlar[3][1], y, tl(kalem["birim_fiyat"]), f_tablo)
        saga_yaz(cizim, kolonlar[4][1], y, tl(kalem["tutar"]), f_tablo)
        if izgara:
            cizim.line([kenar - 10, y + satir_yuksek - 10, G - kenar + 10, y + satir_yuksek - 10],
                       fill=(205, 205, 205), width=1)
        y += satir_yuksek

    y += 30
    # Etiket ile tutar arası, en uzun tutarın ("124.998,07 TL") sığacağı kadar
    # açık olmalı; dar bırakılırsa iki metin üst üste biner.
    etiket_x = G - kenar - 480
    for etiket, deger in [
        ("Ara Toplam", tl(top["ara_toplam"])),
        (f"KDV %{top['kdv_orani']}", tl(top["kdv"])),
    ]:
        cizim.text((etiket_x, y), etiket, font=f_orta, fill=(60, 60, 60))
        saga_yaz(cizim, G - kenar, y, f"{deger} TL", f_orta)
        y += 38

    cizim.line([etiket_x, y + 4, G - kenar, y + 4], fill=(60, 60, 60), width=2)
    y += 14
    cizim.text((etiket_x, y), "GENEL TOPLAM", font=font(kalin, 27, rastgele), fill=(15, 15, 15))
    saga_yaz(cizim, G - kenar, y, f"{tl(top['genel_toplam'])} TL", font(kalin, 27, rastgele))

    y += 90
    cizim.text((kenar, y), f"Ödeme şekli: {rastgele.choice(ODEME)}", font=f_kucuk, fill=(90, 90, 90))
    cizim.text((kenar, y + 30), "Bu belge elektronik ortamda düzenlenmiştir.", font=f_kucuk, fill=(150, 150, 150))

    dogru = {
        "belge_turu": "fatura",
        "alanlar": {
            "satici": satici,
            "alici": alici,
            "tarih": tarih_iso,
            "belge_no": belge_no,
            "vergi_no": vergi_no,
            **top,
        },
        "kalemler": kalemler,
    }
    return gorsel, dogru


# ------------------------------ şablon: fiş ------------------------------


def ciz_fis(rastgele: random.Random) -> tuple[Image.Image, dict]:
    """Market fişi: dar, sabit genişlikli, tablo çizgisi yok.

    Faturadan farkı yalnız görünüm değil: kalem satırı iki satıra bölünüyor
    (ürün adı üstte, `adet x fiyat` altta). Tablo hücresi olmayan bu düzeni
    okumak, tabloyu okumaktan başka bir yetenek.
    """
    kalemler = kalemler_uret(rastgele, 2, 7)
    top = toplamlar(kalemler, rastgele.choice([1, 10, 20]))

    G = rastgele.choice([560, 620, 680])
    Y = 300 + len(kalemler) * 74 + 260
    gorsel = Image.new("RGB", (G, Y), (252, 252, 250))
    cizim = ImageDraw.Draw(gorsel)

    tip = rastgele.choice(["consola.ttf", "cour.ttf"])
    f_bas = sabit_font(tip, 30)
    f = sabit_font(tip, 23)
    f_kucuk = sabit_font(tip, 20)
    f_toplam = sabit_font(tip, 28)

    satici = rastgele.choice(FIRMALAR)
    sehir = rastgele.choice(SEHIRLER)
    tarih_metin, tarih_iso = tarih_uret(rastgele)
    belge_no = f"{rastgele.randint(1, 9999):04d}"
    vergi_no = str(rastgele.randint(1000000000, 9999999999))
    kenar = 34
    y = 30

    kisa = buyut(satici.replace(" Ltd. Şti.", "").replace(" A.Ş.", ""))
    ortala_yaz(cizim, G // 2, y, kisa[:26], f_bas)
    y += 42
    ortala_yaz(cizim, G // 2, y, f"{buyut(sehir)} ŞUBESİ", f_kucuk, (70, 70, 70))
    y += 30
    ortala_yaz(cizim, G // 2, y, f"VD: {sehir}  VKN: {vergi_no}", f_kucuk, (70, 70, 70))
    y += 40

    cizim.text((kenar, y), f"TARİH: {tarih_metin}", font=f_kucuk, fill=(40, 40, 40))
    saga_yaz(cizim, G - kenar, y, f"SAAT: {rastgele.randint(8, 21):02d}:{rastgele.randint(0, 59):02d}", f_kucuk)
    y += 28
    cizim.text((kenar, y), f"FİŞ NO: {belge_no}", font=f_kucuk, fill=(40, 40, 40))
    y += 34
    cizim.text((kenar, y), "-" * (G // 13), font=f_kucuk, fill=(120, 120, 120))
    y += 30

    for kalem in kalemler:
        cizim.text((kenar, y), buyut(kalem["urun"][:30]), font=f, fill=(25, 25, 25))
        y += 30
        cizim.text((kenar + 30, y), f"{kalem['adet']} {kalem['birim']} x {tl(kalem['birim_fiyat'])}",
                   font=f_kucuk, fill=(70, 70, 70))
        saga_yaz(cizim, G - kenar, y, f"*{tl(kalem['tutar'])}", f)
        y += 44

    cizim.text((kenar, y), "-" * (G // 13), font=f_kucuk, fill=(120, 120, 120))
    y += 34
    cizim.text((kenar, y), "ARA TOPLAM", font=f, fill=(40, 40, 40))
    saga_yaz(cizim, G - kenar, y, tl(top["ara_toplam"]), f)
    y += 34
    cizim.text((kenar, y), f"KDV %{top['kdv_orani']}", font=f, fill=(40, 40, 40))
    saga_yaz(cizim, G - kenar, y, tl(top["kdv"]), f)
    y += 40
    cizim.text((kenar, y), "TOPLAM", font=f_toplam, fill=(10, 10, 10))
    saga_yaz(cizim, G - kenar, y, tl(top["genel_toplam"]), f_toplam)
    y += 50
    odeme = rastgele.choice(ODEME)
    # Etiketli basılıyor: etiketsiz tek bir kelimenin ("ÇEK") ödeme türü olduğunu modelin
    # bilmesini beklemek haksızdı, ilk ölçümde 12/24 belgede null döndürmesinin sebebi buydu.
    cizim.text((kenar, y), f"ÖDEME: {buyut(odeme)}", font=f_kucuk, fill=(60, 60, 60))
    y += 40
    ortala_yaz(cizim, G // 2, y, "TEŞEKKÜR EDERİZ", f_kucuk, (90, 90, 90))

    dogru = {
        "belge_turu": "fis",
        "alanlar": {
            # Fişte firma adı KISALTILMIŞ ve BÜYÜK harfle basılıyor; doğru cevap belgede
            # yazan şey olmalı, üretirken kullandığımız uzun ad değil. Aksi hâlde model
            # belgeyi doğru okuduğu hâlde hata alır — ölçüm kusuru, tam koşu 2'de 31 kalemi
            # ayıklamak zorunda kaldığımız türden (bkz. training/kosu2_hata_etiketleri.md).
            "satici": kisa[:26],
            "tarih": tarih_iso,
            "belge_no": belge_no,
            "vergi_no": vergi_no,
            "odeme": odeme,
            **top,
        },
        "kalemler": kalemler,
    }
    return gorsel, dogru


# ------------------------------ şablon: makbuz ------------------------------


def ciz_makbuz(rastgele: random.Random) -> tuple[Image.Image, dict]:
    """Gider pusulası: kalem tablosu YOK, yalnız belge düzeyi alanlar.

    "Kaç satır yazılacağına model karar verir" kararının sınavı bu şablon: doğru
    davranış tek satır üretmek. Kalem tablosu uydurursa burada görülür.
    """
    G, Y = 1240, 880
    gorsel = Image.new("RGB", (G, Y), (255, 254, 250))
    cizim = ImageDraw.Draw(gorsel)

    f_bas = font("kalin", 36, rastgele)
    f = font("duz", 27, rastgele)
    f_kucuk = font("duz", 22, rastgele)

    odenen = rastgele.choice(ALICILAR + FIRMALAR)
    tarih_metin, tarih_iso = tarih_uret(rastgele)
    belge_no = f"GP-{rastgele.randint(1000, 9999)}"
    aciklama = rastgele.choice([
        "Şantiye nakliye bedeli",
        "Araç bakım ve onarım gideri",
        "Ofis temizlik hizmeti",
        "Yükleme boşaltma işçiliği",
        "Depo kira ödemesi",
        "Elektrik tesisat tamiri",
    ])
    tutar = round(rastgele.uniform(250, 48000), 2)
    stopaj_orani = rastgele.choice([0, 20])
    stopaj = round(tutar * stopaj_orani / 100, 2)
    net = round(tutar - stopaj, 2)

    kenar = 100
    cizim.rectangle([kenar - 30, 60, G - kenar + 30, Y - 60], outline=(120, 120, 120), width=3)
    ortala_yaz(cizim, G // 2, 100, "GİDER PUSULASI", f_bas)
    y = 200

    for etiket, deger in [
        ("Ödeme yapılan", odenen),
        ("Tarih", tarih_metin),
        ("Belge No", belge_no),
        ("Açıklama", aciklama),
    ]:
        cizim.text((kenar, y), f"{etiket}", font=f_kucuk, fill=(120, 120, 120))
        cizim.text((kenar + 260, y - 4), deger, font=f, fill=(20, 20, 20))
        cizim.line([kenar + 250, y + 34, G - kenar, y + 34], fill=(210, 210, 210), width=1)
        y += 66

    y += 30
    for etiket, deger in [
        ("Brüt tutar", tl(tutar)),
        (f"Stopaj %{stopaj_orani}", tl(stopaj)),
        ("Net ödenen", tl(net)),
    ]:
        cizim.text((kenar + 500, y), etiket, font=f, fill=(60, 60, 60))
        saga_yaz(cizim, G - kenar, y, f"{deger} TL", f)
        y += 44

    cizim.text((kenar, Y - 150), "İmza", font=f_kucuk, fill=(120, 120, 120))
    cizim.line([kenar, Y - 110, kenar + 260, Y - 110], fill=(160, 160, 160), width=1)

    dogru = {
        "belge_turu": "makbuz",
        "alanlar": {
            "odenen": odenen,
            "tarih": tarih_iso,
            "belge_no": belge_no,
            "aciklama": aciklama,
            "brut_tutar": tutar,
            "stopaj_orani": stopaj_orani,
            "stopaj": stopaj,
            "net_tutar": net,
        },
        "kalemler": [],
    }
    return gorsel, dogru


# ------------------------------ şablon: serbest not ------------------------------


def ciz_not(rastgele: random.Random) -> tuple[Image.Image, dict]:
    """Kâğıda karalanmış not: "Ahmet 100".

    Bu şablonun varlık sebebi bir kapsam düzeltmesi: yüklenen şey fatura/fiş/makbuz olmak
    zorunda değil. Ne matbu, ne etiketli, ne toplamı var — model burada tabloyu değil
    "ne yazdığını" okumak zorunda. Alanların bir kısmı YOK (başlık ya da tarih yazılmamış);
    doğru davranış null döndürmek, uydurmak değil.
    """
    satir_sayisi = rastgele.choice([1, 1, 2, 3, 4, 5])
    G = rastgele.choice([700, 820, 900])
    Y = 240 + satir_sayisi * 78

    kagit = rastgele.choice([(253, 251, 240), (250, 250, 250), (245, 243, 228)])
    gorsel = Image.new("RGB", (G, Y), kagit)
    cizim = ImageDraw.Draw(gorsel)

    # Kimi kâğıt çizgili: model çizgiyi metin sanmamalı.
    if rastgele.random() < 0.45:
        for cy in range(120, Y - 20, 60):
            cizim.line([30, cy, G - 30, cy], fill=(205, 215, 235), width=2)

    tip = rastgele.choice(EL_YAZISI)
    f_bas = sabit_font(tip, rastgele.choice([46, 52]))
    f = sabit_font(tip, rastgele.choice([34, 38, 42]))

    kenar = rastgele.choice([50, 70])
    y = 40

    baslik = rastgele.choice(NOT_BASLIKLARI) if rastgele.random() < 0.6 else None
    if baslik:
        cizim.text((kenar, y), baslik, font=f_bas, fill=(30, 40, 90))
        y += 70

    tarih_metin, tarih_iso = tarih_uret(rastgele)
    tarih_var = rastgele.random() < 0.5
    if tarih_var:
        saga_yaz(cizim, G - kenar, y - 60 if baslik else y, tarih_metin, f, (40, 40, 40))
        if not baslik:
            y += 60

    kalemler = []
    for kisi in rastgele.sample(KISILER, satir_sayisi):
        tutar = rastgele.choice([
            float(rastgele.randint(1, 40) * 50),          # 100, 250, 2000
            round(rastgele.uniform(10, 3000), 2),
        ])
        # Yazım biçimi satır satır değişiyor: "100", "100 TL", "1.250,50"
        bicim = rastgele.random()
        metin = tl(tutar) if bicim < 0.4 else (
            f"{tl(tutar)} TL" if bicim < 0.7 else f"{int(tutar)}" if tutar == int(tutar) else tl(tutar))

        # El yazısını taklit için satır başı ve yazı boyutu hafif kayıyor.
        sapma = rastgele.randint(-6, 10)
        f_satir = sabit_font(tip, f.size + rastgele.choice([-2, 0, 0, 2]))
        cizim.text((kenar + sapma, y), kisi, font=f_satir, fill=(25, 25, 35))
        saga_yaz(cizim, G - kenar - rastgele.randint(0, 20), y, metin, f_satir, (25, 25, 35))
        y += 78

        kalemler.append({"kisi": kisi, "tutar": round(tutar, 2)})

    return gorsel, {
        "belge_turu": "not",
        "alanlar": {"baslik": baslik, "tarih": tarih_iso if tarih_var else None},
        "kalemler": kalemler,
    }


# ------------------------------ şablon: başlıksız liste ------------------------------


def ciz_liste(rastgele: random.Random) -> tuple[Image.Image, dict]:
    """Kolon başlığı OLMAYAN tablo.

    CSV'de kolon adını başlık satırı verir; burada vermiyor. Model kolonu adından değil
    içeriğinden tanımak zorunda: hangi sütun kişi, hangisi adet, hangisi tutar.
    """
    G, Y = 900, 240 + rastgele.randint(3, 6) * 62
    gorsel = Image.new("RGB", (G, Y), (252, 252, 250))
    cizim = ImageDraw.Draw(gorsel)

    f = font("duz", 28, rastgele)
    kenar = 70
    y = 90

    baslik = rastgele.choice(NOT_BASLIKLARI) if rastgele.random() < 0.5 else None
    if baslik:
        cizim.text((kenar, 35), baslik, font=font("kalin", 34, rastgele), fill=(20, 20, 20))

    satir_sayisi = (Y - 240) // 62
    kalemler = []
    for kisi in rastgele.sample(KISILER, satir_sayisi):
        adet = rastgele.randint(1, 30)
        tutar = round(rastgele.uniform(20, 5000), 2)
        cizim.text((kenar, y), kisi, font=f, fill=(25, 25, 25))
        saga_yaz(cizim, kenar + 480, y, str(adet), f)
        saga_yaz(cizim, G - kenar, y, tl(tutar), f)

        # Elle çizilmiş izlenimi için çizgiler hafif kaykılıyor.
        cizim.line([kenar - 20, y + 44, G - kenar + 20, y + 44 + rastgele.randint(-2, 2)],
                   fill=(190, 190, 190), width=1)
        y += 62
        kalemler.append({"kisi": kisi, "adet": adet, "tutar": tutar})

    return gorsel, {
        "belge_turu": "liste",
        "alanlar": {"baslik": baslik},
        "kalemler": kalemler,
    }


# ------------------------------ fotoğraf bozulması ------------------------------


def _perspektif_katsayi(kaynak, hedef):
    """PIL'in PERSPECTIVE dönüşümü 8 katsayı ister; köşe eşlemesinden çözülür."""
    matris = []
    for (hx, hy), (kx, ky) in zip(hedef, kaynak):
        matris.append([hx, hy, 1, 0, 0, 0, -kx * hx, -kx * hy])
        matris.append([0, 0, 0, hx, hy, 1, -ky * hx, -ky * hy])
    A = np.array(matris, dtype=float)
    B = np.array(kaynak, dtype=float).reshape(8)
    return np.linalg.solve(A, B)


def fotografa_cevir(sayfa: Image.Image, rastgele: random.Random) -> Image.Image:
    """Temiz çizimi telefonla çekilmiş belgeye yaklaştırır.

    Sıra fiziksel: önce sayfa masaya konur ve eğrilir (geometri), sonra ışık
    düşer (gölge/parlama), en son fotoğraf makinesi araya girer (bulanıklık,
    gürültü, JPEG). Ters sırada uygulanırsa gürültü de perspektifle birlikte
    esner ve gerçekte olmayan bir doku çıkar.
    """
    # 1) Sayfa bir zemine konuyor: model önce belgeyi bulmak zorunda kalsın.
    pay = int(min(sayfa.size) * rastgele.uniform(0.06, 0.16))
    zemin_tonu = rastgele.choice([(118, 106, 94), (140, 140, 145), (92, 84, 76), (165, 158, 148)])
    zemin = Image.new("RGB", (sayfa.width + 2 * pay, sayfa.height + 2 * pay), zemin_tonu)

    doku = np.array(zemin, dtype=np.int16)
    doku += rastgele.randint(6, 14) * np.random.default_rng(rastgele.randint(0, 10**6)).standard_normal(
        doku.shape
    ).astype(np.int16)
    zemin = Image.fromarray(np.clip(doku, 0, 255).astype(np.uint8))
    zemin.paste(sayfa, (pay, pay))
    gorsel = zemin

    # 2) Hafif dönme (elde tutulan telefon hiç düz durmaz).
    gorsel = gorsel.rotate(rastgele.uniform(-3.5, 3.5), resample=Image.BICUBIC,
                           expand=True, fillcolor=zemin_tonu)

    # 3) Perspektif: köşeler kısa kenarın %4'üne kadar kayıyor.
    G, Y = gorsel.size
    kay = min(G, Y) * rastgele.uniform(0.01, 0.045)
    koseler = [(0, 0), (G, 0), (G, Y), (0, Y)]
    hedef = [(x + rastgele.uniform(-kay, kay), y + rastgele.uniform(-kay, kay)) for x, y in koseler]
    katsayi = _perspektif_katsayi(koseler, hedef)
    gorsel = gorsel.transform((G, Y), Image.PERSPECTIVE, katsayi, resample=Image.BICUBIC,
                              fillcolor=zemin_tonu)

    dizi = np.array(gorsel, dtype=np.float32)

    # 4) Eşit olmayan aydınlatma: bir köşeden gelen ışık + karşı köşede gölge.
    yy, xx = np.mgrid[0:Y, 0:G].astype(np.float32)
    yon = rastgele.uniform(0, 2 * math.pi)
    egim = (math.cos(yon) * xx / G + math.sin(yon) * yy / Y)
    parlaklik = 1.0 + rastgele.uniform(0.10, 0.30) * (egim - egim.mean())
    dizi *= parlaklik[:, :, None]

    # 5) Elin/telefonun düşürdüğü sert kenarlı gölge şeridi.
    if rastgele.random() < 0.6:
        golge = np.ones((Y, G), dtype=np.float32)
        if rastgele.random() < 0.5:
            sinir = int(G * rastgele.uniform(0.55, 0.9))
            golge[:, sinir:] = rastgele.uniform(0.55, 0.8)
        else:
            sinir = int(Y * rastgele.uniform(0.6, 0.9))
            golge[sinir:, :] = rastgele.uniform(0.55, 0.8)
        golge = np.array(Image.fromarray((golge * 255).astype(np.uint8)).filter(
            ImageFilter.GaussianBlur(radius=min(G, Y) * 0.03)), dtype=np.float32) / 255.0
        dizi *= golge[:, :, None]

    gorsel = Image.fromarray(np.clip(dizi, 0, 255).astype(np.uint8))

    # 6) Odak kaçığı + sensör gürültüsü.
    gorsel = gorsel.filter(ImageFilter.GaussianBlur(radius=rastgele.uniform(0.4, 1.3)))
    dizi = np.array(gorsel, dtype=np.float32)
    dizi += np.random.default_rng(rastgele.randint(0, 10**6)).standard_normal(dizi.shape).astype(
        np.float32
    ) * rastgele.uniform(3, 9)
    gorsel = Image.fromarray(np.clip(dizi, 0, 255).astype(np.uint8))

    # 7) Telefon çözünürlüğüne indir: ölçüm gerçekte gelecek boyutta yapılsın.
    hedef_uzun = rastgele.choice([1200, 1600, 2000])
    olcek = hedef_uzun / max(gorsel.size)
    if olcek < 1:
        gorsel = gorsel.resize((int(gorsel.width * olcek), int(gorsel.height * olcek)), Image.LANCZOS)

    return gorsel


# ------------------------------ akış ------------------------------


# Sıra önemli: yeni şablonlar SONA eklenir. Rastgele akışı şablon şablon ilerlediği için
# başa eklemek eski şablonların görüntülerini de değiştirir ve kaydedilmiş model
# yanıtlarını geçersiz kılar.
SABLONLAR = {
    "fatura": ciz_fatura,
    "fis": ciz_fis,
    "makbuz": ciz_makbuz,
    "not": ciz_not,
    "liste": ciz_liste,
}


def main() -> None:
    ayristirici = argparse.ArgumentParser(description="Sentetik belge ölçüm kümesi üretir.")
    ayristirici.add_argument("--out", default="data/belgeler", help="çıktı dizini")
    ayristirici.add_argument("--adet", type=int, default=30, help="şablon başına belge sayısı")
    ayristirici.add_argument("--seed", type=int, default=42)
    ayristirici.add_argument("--kalite", type=int, default=72, help="foto varyantı JPEG kalitesi")
    secenek = ayristirici.parse_args()

    cikti = Path(secenek.out)
    (cikti / "temiz").mkdir(parents=True, exist_ok=True)
    (cikti / "foto").mkdir(parents=True, exist_ok=True)

    rastgele = random.Random(secenek.seed)
    kayitlar = []

    for sablon, ciz in SABLONLAR.items():
        for sira in range(1, secenek.adet + 1):
            kimlik_ = kimlik(sablon, sira)
            sayfa, dogru = ciz(rastgele)

            temiz_yol = cikti / "temiz" / f"{kimlik_}.jpg"
            sayfa.save(temiz_yol, quality=95)

            foto_yol = cikti / "foto" / f"{kimlik_}.jpg"
            fotografa_cevir(sayfa, rastgele).save(foto_yol, quality=secenek.kalite)

            for varyant, yol in [("temiz", temiz_yol), ("foto", foto_yol)]:
                kayitlar.append({
                    "id": f"{kimlik_}_{varyant}",
                    "sablon": sablon,
                    "varyant": varyant,
                    "dosya": str(yol.relative_to(cikti)).replace("\\", "/"),
                    **dogru,
                })

    # Doğru cevaplar tek dosyada: ölçüm aracı görüntüyü değil bu satırı okuyacak.
    dogru_yol = cikti / "dogru.jsonl"
    with dogru_yol.open("w", encoding="utf-8") as akis:
        for kayit in kayitlar:
            akis.write(json.dumps(kayit, ensure_ascii=False) + "\n")

    kalemli = sum(1 for k in kayitlar if k["kalemler"])
    print(f"{len(kayitlar)} kayıt ({len(SABLONLAR)} şablon × {secenek.adet} × 2 varyant)")
    print(f"  kalem tablosu olan : {kalemli}")
    print(f"  kalemsiz (makbuz)  : {len(kayitlar) - kalemli}")
    print(f"  görüntüler         : {cikti / 'temiz'} , {cikti / 'foto'}")
    print(f"  doğru cevaplar     : {dogru_yol}")


if __name__ == "__main__":
    main()
