import 'package:flutter_test/flutter_test.dart';
import 'package:veriyonetim_client/screens/watches_screen.dart';

// İzleyici arayüzünün iki saf parçası: eşik metninin sayıya çevrilmesi ve koşulun
// okunur hâli. İkisi de ekrana bağlı olmadığı için doğrudan sınanabiliyor.
void main() {
  group('Eşik metni sayıya çevrilirken', () {
    // Asıl mesele: "1.500" ile "1.5" ikisi de nokta içeriyor ama biri bin beş yüz,
    // diğeri bir buçuk. Ayrım yapılmazsa kullanıcı eşiğini bin kat yanlış kurar ve
    // bunu ancak alarm hiç çalmayınca fark eder.
    test('Türkçe binlik ayracı sayıyı bozmuyor', () {
      expect(parseUserNumber('1.500'), 1500);
      expect(parseUserNumber('1.500.000'), 1500000);
    });

    test('nokta ondalık ayracı olarak da okunabiliyor', () {
      expect(parseUserNumber('1.5'), 1.5);
      expect(parseUserNumber('0.25'), 0.25);
    });

    // Kod incelemesinde bulunan TERS YÖNDEKİ kusur. Eski kural yalnız "noktadan sonra
    // üç hane var mı" diye bakıyordu, dolayısıyla üç ondalıklı gerçek sayıları da
    // binlik sanıp bin katına çıkarıyordu: "0.125" → 125. Kullanıcı hata oranı için
    // 0,125 eşiği kuruyor, sunucuya 125 gidiyor ve alarm hiç çalmıyordu — yani kuralın
    // önlemek için yazıldığı belirtinin ta kendisi, diğer yönden.
    test('SIFIRLA başlayan üç ondalıklı sayı binlik sanılmıyor', () {
      // Ayrımın dayanağı baştaki sıfır: hiçbir binlik yazım "0." ile başlamaz, yani
      // burada belirsizlik YOK. Oran ve hata payı eşikleri tam olarak bu biçimde
      // yazıldığı için kusurun görüldüğü yer de burasıydı.
      expect(parseUserNumber('0.125'), 0.125);
      expect(parseUserNumber('0.500'), 0.5);
    });

    test('noktadan sonra üçten fazla hane varsa ondalıktır', () {
      expect(parseUserNumber('1.5000'), 1.5);
    });

    test('binlik ayracı ancak TAM kalıba uyarsa siliniyor', () {
      expect(parseUserNumber('1.500'), 1500);
      expect(parseUserNumber('12.345.678'), 12345678);
    });

    // BİLİNEN BELİRSİZLİK, gizlenmiyor: "1.250" hem bin iki yüz elli hem bir virgül iki
    // beş olabilir ve metnin kendisinde ayırt edecek bir işaret yok. Kural Türkçe
    // okumayı seçiyor (binlik), çünkü eşik alanına yazılan sayılar ezici çoğunlukla
    // tutar. Ondalık isteyen kullanıcı virgülle yazınca ("1,25") tereddüt kalmıyor.
    test('sıfırla başlamayan üç haneli grup binlik okunuyor (bilinen belirsizlik)', () {
      expect(parseUserNumber('1.250'), 1250);
      expect(parseUserNumber('1,25'), 1.25);
    });

    test('virgül varsa ondalık ayracı odur, nokta binliktir', () {
      expect(parseUserNumber('1.500,25'), 1500.25);
      expect(parseUserNumber('1000,5'), 1000.5);
    });

    test('boş ve anlamsız metin sayı değil', () {
      expect(parseUserNumber(''), isNull);
      expect(parseUserNumber('   '), isNull);
      expect(parseUserNumber('bin'), isNull);
    });

    test('eksi değer ve boşluklu yazım kabul ediliyor', () {
      expect(parseUserNumber(' -250 '), -250);
    });
  });

  group('Koşul yazısı', () {
    test('mutlak değer ile yüzde değişimi ayırıyor', () {
      expect(watchConditionLabel('value', 'lt', 1000),
          'Değer 1.000 altına inerse');
      expect(watchConditionLabel('change', 'gt', 20),
          'Değişim %20 üzerine çıkarsa');
    });
  });
}
