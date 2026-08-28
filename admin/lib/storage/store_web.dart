import 'dart:js_interop';
import 'dart:js_interop_unsafe';

import 'package:web/web.dart' as web;

import 'store_api.dart';

// Tarayıcı gerçeklemesi. Bu dosya YALNIZ web hedefinde derlenir (bkz. store.dart).

class _WebStore implements KeyValueStore {
  final web.Storage _storage;

  const _WebStore(this._storage);

  @override
  String? getItem(String key) => _storage.getItem(key);

  @override
  void setItem(String key, String value) => _storage.setItem(key, value);

  @override
  void removeItem(String key) => _storage.removeItem(key);
}

/// Sekme kapanınca uçar. Panel için bilinçli tercih: en yetkili kimliğe en kısa tasma.
KeyValueStore get sessionStore => _WebStore(web.window.sessionStorage);

/// Sunucunun adresi — `config.js`in yazdığı `window.API_BASE_URL`.
///
/// Tanımsızsa (geliştirmede) null döner. `getProperty` ile okunuyor çünkü "değişken
/// tanımlı değil" burada geçerli bir durum; `external` bir bildirim çalışma anında
/// hata verirdi.
String? get configuredApiBaseUrl {
  final value = globalContext.getProperty<JSAny?>('API_BASE_URL'.toJS);
  if (value == null) return null;

  final text = (value as JSString).toDart.trim();
  if (text.isEmpty) return null;

  // Adresler '$baseUrl/api/...' diye kurulduğu için sondaki '/' düşürülüyor.
  return text.endsWith('/') ? text.substring(0, text.length - 1) : text;
}
