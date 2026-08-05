// Platform seçici. Panel kodu HER ZAMAN bu dosyayı import eder.
//
// `dart.library.js_interop` yalnız web hedefinde doğrudur; Dart VM'de (yani
// `flutter test` altında) yanlıştır — testler tarayıcı API'sine hiç dokunmaz.
export 'store_api.dart';
export 'store_stub.dart' if (dart.library.js_interop) 'store_web.dart';
