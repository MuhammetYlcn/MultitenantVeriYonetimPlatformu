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
