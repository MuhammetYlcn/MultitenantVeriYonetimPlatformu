import 'platform_api.dart';

// Dart VM gerçeklemesi — pratikte `flutter test` altında kullanılır.
// Tarayıcı API'si yok, bu yüzden her yetenek kendi doğru karşılığını verir.

/// VM'de dosya seçme penceresi diye bir şey yok.
///
/// Bilerek istisna fırlatıyor: null döndürmek "kullanıcı iptal etti" demek olurdu
/// ve çağıran taraf (`_uploadFile`) sessizce hiçbir şey yapmadan dönerdi. Böyle bir
/// sessizlik, uygulama bir gün masaüstünde koşturulursa hata olarak fark edilmez.
Future<PickedFile?> pickCsvOrExcelFile() async {
  throw UnsupportedError(
      'Dosya seçme yalnız web hedefinde çalışır (platform_web.dart).');
}

/// Belge görüntüsü seçme — yukarıdakiyle aynı gerekçe.
Future<PickedFile?> pickImageFile() async {
  throw UnsupportedError(
      'Dosya seçme yalnız web hedefinde çalışır (platform_web.dart).');
}

// Bellekte tutulan depo. Testler token yazma/okuma akışını gerçek tarayıcı olmadan
// koşabilsin diye atmak yerine çalışan bir karşılık veriyor; süreç bitince uçar.
class _MemoryStore implements KeyValueStore {
  final Map<String, String> _values = {};

  @override
  String? getItem(String key) => _values[key];

  @override
  void setItem(String key, String value) => _values[key] = value;

  @override
  void removeItem(String key) => _values.remove(key);
}

final KeyValueStore _local = _MemoryStore();
final KeyValueStore _session = _MemoryStore();

KeyValueStore get localStore => _local;
KeyValueStore get sessionStore => _session;
