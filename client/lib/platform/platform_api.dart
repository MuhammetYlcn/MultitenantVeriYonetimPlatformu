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
