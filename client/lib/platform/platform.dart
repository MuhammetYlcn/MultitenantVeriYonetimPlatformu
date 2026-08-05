// Platform seçici. Uygulama kodu HER ZAMAN bu dosyayı import eder.
//
// `dart.library.js_interop` yalnız web hedefinde derlenirken doğrudur; Dart VM'de
// (yani `flutter test` altında) yanlıştır. Böylece testler tarayıcı API'lerine hiç
// dokunmaz ve `package:web` VM'de derlenmeye çalışılmaz.
export 'platform_api.dart';
export 'platform_stub.dart' if (dart.library.js_interop) 'platform_web.dart';
