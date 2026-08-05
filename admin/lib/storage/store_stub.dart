import 'store_api.dart';

// Dart VM gerçeklemesi — pratikte `flutter test` altında kullanılır.

// Bellekte tutulan depo: testler oturum akışını gerçek tarayıcı olmadan koşabilsin
// diye atmak yerine çalışan bir karşılık veriyor; süreç bitince uçar.
class _MemoryStore implements KeyValueStore {
  final Map<String, String> _values = {};

  @override
  String? getItem(String key) => _values[key];

  @override
  void setItem(String key, String value) => _values[key] = value;

  @override
  void removeItem(String key) => _values.remove(key);
}

final KeyValueStore _session = _MemoryStore();

KeyValueStore get sessionStore => _session;
