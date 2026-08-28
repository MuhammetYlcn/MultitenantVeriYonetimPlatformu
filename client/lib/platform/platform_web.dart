import 'dart:async';
import 'dart:js_interop';
import 'dart:js_interop_unsafe';

import 'package:web/web.dart' as web;

import 'platform_api.dart';

// Tarayıcı gerçeklemesi. Bu dosya YALNIZ web hedefinde derlenir (bkz. platform.dart).

/// Tarayıcıda CSV/Excel dosyası seçtirir; iptal edilirse null döner.
///
/// Neden file_picker paketi değil: file_picker 11.0.2'nin web tarafı oluşturduğu
/// `<input type="file">` öğesini belgeye EKLEMEDEN `click()` çağırıyor. Chrome,
/// belgeye bağlı olmayan bir dosya girdisinin click'ini yok sayar; dosya penceresi
/// hiç açılmaz ve buton çalışmıyormuş gibi görünür. Burada girdi gerçekten
/// `document.body`'ye ekleniyor (`appendChild`), tıklanıyor ve iş bitince siliniyor.
Future<PickedFile?> pickCsvOrExcelFile() => _pickFile('.csv,.xlsx');

/// Belge görüntüsü seçtirir (fatura/fiş fotoğrafı). Kabul edilen türler sunucudaki
/// beyaz listeyle aynı tutulmalı; aksi halde kullanıcı seçtiği dosyayı yükleyip
/// 400 alır.
Future<PickedFile?> pickImageFile() => _pickFile('.jpg,.jpeg,.png,.webp');

Future<PickedFile?> _pickFile(String accept) async {
  final input = web.HTMLInputElement()
    ..type = 'file'
    ..accept = accept
    ..multiple = false;
  // Görünmez ama DOM'da: tarayıcının dosya penceresini açması için bağlı olmalı.
  input.style.display = 'none';
  web.document.body!.appendChild(input);

  final completer = Completer<PickedFile?>();

  void finish(PickedFile? result) {
    if (completer.isCompleted) return;
    if (input.isConnected) input.remove();
    completer.complete(result);
  }

  input.addEventListener(
      'change',
      ((web.Event _) {
        final files = input.files;
        if (files == null || files.length == 0) {
          finish(null);
          return;
        }
        final file = files.item(0)!;
        // Dosya içeriği asenkron okunur: FileReader bitince 'load' olayı gelir.
        final reader = web.FileReader();
        reader.addEventListener(
            'load',
            ((web.Event _) {
              final buffer = reader.result as JSArrayBuffer;
              finish(PickedFile(file.name, buffer.toDart.asUint8List()));
            }).toJS);
        reader.addEventListener('error', ((web.Event _) => finish(null)).toJS);
        reader.readAsArrayBuffer(file);
      }).toJS);

  // Kullanıcı pencereyi kapatırsa 'change' hiç gelmez; 'cancel' olmadan sonsuz beklerdik.
  input.addEventListener('cancel', ((web.Event _) => finish(null)).toJS);

  input.click();
  return completer.future;
}

// web.Storage'ı KeyValueStore yüzüne saran ince kabuk.
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

/// Sekme/tarayıcı kapansa da kalır — "oturumu açık tut" için.
KeyValueStore get localStore => _WebStore(web.window.localStorage);

/// Yenilemede kalır ama sekme kapanınca uçar — işaretsiz mod.
KeyValueStore get sessionStore => _WebStore(web.window.sessionStorage);

/// Sunucunun adresi — `config.js`in yazdığı `window.API_BASE_URL` değeri.
///
/// Değişken tanımsızsa (geliştirmede: depodaki config.js hiçbir şey tanımlamaz) null
/// döner ve çağıran taraf geliştirme varsayılanına düşer. Konteynerde dosyayı açılışta
/// panel imajı üretiyor, bu yüzden değer orada her zaman dolu olur.
///
/// `getProperty` ile okunuyor, `external` bir bildirimle değil: tanımsız bir genel
/// değişkeni `external` üye olarak okumak çalışma anında hata verirdi, oysa "değişken
/// yok" burada geçerli bir durum — geliştirmede beklenen durum.
String? get configuredApiBaseUrl {
  final value = globalContext.getProperty<JSAny?>('API_BASE_URL'.toJS);
  if (value == null) return null;

  // Boş metin de "ayarlanmadı" sayılıyor: config.js'te `"" || window.location.origin`
  // yazıyor, yani boş değer zaten sayfanın kendi adresine dönüşür. Buraya boş bir
  // metnin gelmesi ancak dosyanın elle bozulmasıyla mümkün; o hâlde de sessizce
  // geçersiz bir adrese istek atmaktansa varsayılana düşmek doğru davranış.
  final text = (value as JSString).toDart.trim();
  if (text.isEmpty) return null;

  // Sondaki '/' düşürülüyor: adresler '$baseUrl/api/...' diye kuruluyor, aksi hâlde
  // '//api/...' oluşur.
  return text.endsWith('/') ? text.substring(0, text.length - 1) : text;
}
