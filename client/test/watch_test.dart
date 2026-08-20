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
