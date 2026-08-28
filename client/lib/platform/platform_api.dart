import 'dart:typed_data';

// Tarayıcıya özgü yeteneklerin platformdan BAĞIMSIZ yüzü.
//
// Neden bu dosya var: uygulama tarayıcıda çalışıyor ve iki yerde doğrudan tarayıcı
// API'si kullanıyordu (dosya seçme, localStorage). Bunlar `dart:js_interop` ve
// `package:web` üzerinden gelir; bu kütüphaneler yalnız web hedefinde derlenir.
// Doğrudan import edildiklerinde `flutter test` (Dart VM hedefi) uygulamayı hiç
// derleyemiyordu — yani arayüz testleri kod yazılmış olmasına rağmen koşamıyordu.
//
// Çözüm: somut tarayıcı çağrıları `platform_web.dart`e alındı, VM için sade bir
// karşılığı `platform_stub.dart`ta duruyor; `platform.dart` hangisinin
// derleneceğine koşullu export ile karar veriyor. Uygulama kodu yalnız buradaki
// tipleri görür.

/// Seçilen dosya: adı (uzantısı sunucudaki ayrıştırıcı için önemli) + ham içeriği.
class PickedFile {
  final String name;
  final Uint8List bytes;

  PickedFile(this.name, this.bytes);
}

/// Anahtar-değer deposu. Tarayıcıda `localStorage` / `sessionStorage` karşılığı.
abstract class KeyValueStore {
  String? getItem(String key);
  void setItem(String key, String value);
  void removeItem(String key);
}

// Bu dosyada TANIM olarak durmayan, ama iki gerçeklemenin de sağladığı bir üye daha var:
//
//   String? get configuredApiBaseUrl
//
// Sunucunun adresi. Tarayıcıda `config.js` dosyasının yazdığı `window.API_BASE_URL`
// değerinden gelir, Dart VM'de (testlerde) her zaman null'dır. Adres kodda sabit
// olsaydı imaj bir kez derlendikten sonra başka bir sunucuda kullanılamazdı; ayrıntı
// için docker/panel-entrypoint.sh'e bakın.
//
// Neden burada `abstract` bir tanımı yok: bunlar sınıf değil, üst düzey (top-level)
// üyeler; koşullu export ikisinden birini seçiyor ve sözleşmeyi derleyici zaten
// çağrı yerinde denetliyor (aynı yol `localStore`/`sessionStore` için de böyle).
