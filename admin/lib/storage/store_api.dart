// Oturum deposunun platformdan BAĞIMSIZ yüzü.
//
// Neden ayrı: depo tarayıcıda `sessionStorage`tır ve `package:web` üzerinden gelir;
// o kütüphane yalnız web hedefinde derlenir. Doğrudan import edildiğinde
// `flutter test` (Dart VM) paneli hiç derleyemiyor, yazılmış smoke test koşamıyordu.
// Somut karşılıklar store_web.dart / store_stub.dart'ta; seçimi store.dart yapar.

/// Anahtar-değer deposu. Tarayıcıda `sessionStorage` karşılığı.
abstract class KeyValueStore {
  String? getItem(String key);
  void setItem(String key, String value);
  void removeItem(String key);
}

// Burada TANIM olarak durmayan ama iki gerçeklemenin de sağladığı bir üye daha:
//
//   String? get configuredApiBaseUrl
//
// Sunucunun adresi; tarayıcıda `config.js`in yazdığı `window.API_BASE_URL`, Dart VM'de
// (testlerde) null. Müşteri panelindeki karşılığıyla aynı gerekçe — adres kodda sabit
// olsaydı imaj yeniden derlenmeden başka bir sunucuda kullanılamazdı.
