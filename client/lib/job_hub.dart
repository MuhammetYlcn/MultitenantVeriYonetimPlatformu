import 'dart:async';

import 'package:signalr_netcore/signalr_client.dart';

import 'api_service.dart';

/// Sunucudan gelen canlı bildirimlerin tek kanalı: belge işleri ve izleyici uyarıları.
///
/// İkisi tek bağlantıda taşınıyor çünkü ayırmanın karşılığı ikinci bir soket ve ikinci
/// bir kimlik doğrulaması olurdu; ayrıştırma sunucu tarafında zaten grup adıyla yapılıyor
/// (iş bildirimi kullanıcıya, izleyici uyarısı firmaya).
///
/// Neden yoklama değil: belge okuma 30-150 saniye sürüyor ve süre baştan bilinmiyor.
/// Yoklamada istemci ya sık sorup boşa istek yığar ya seyrek sorup kullanıcıyı bekletir.
/// Açık kanalda sunucu hazır olduğu anda söylüyor.
///
/// Kanal bir KOLAYLIKTIR, doğruluk kaynağı değil: bağlantı kurulamazsa ekran kilitlenmez,
/// durum `ApiService.getDocumentJob` ile okunmaya devam eder. Bu ayrım bilinçli — canlı
/// bağlantıyı zorunlu kılmak, ağ engeli olan bir kullanıcıda özelliği tümden çalışmaz
/// hâle getirirdi.
class JobHub {
  static HubConnection? _connection;

  /// Gelen iş bildirimleri. Ekranlar buna abone oluyor.
  static final _controller = StreamController<JobNotification>.broadcast();
  static Stream<JobNotification> get updates => _controller.stream;

  /// İzleyici uyarıları. İş bildiriminden AYRI bir akış: alıcısı farklı (iş bildirimi
  /// onu başlatan kişiye, izleyici uyarısı firmanın tamamına gider) ve dinleyeni farklı
  /// (uyarıyı kabuk dinliyor, sohbet ekranı değil).
  static final _alerts = StreamController<WatchAlertNotification>.broadcast();
  static Stream<WatchAlertNotification> get watchAlerts => _alerts.stream;

  /// Bağlantı koptuktan sonra yeniden kurulduğunda tetiklenir.
  ///
  /// Kopukluk penceresinde gönderilen bildirimler KAYIPTIR; kanal onları tekrarlamaz.
  /// Bu akışı dinleyen ekranlar durumlarını sunucudan yeniden okuyarak telafi ediyor.
  static final _reconnected = StreamController<void>.broadcast();
  static Stream<void> get reconnected => _reconnected.stream;

  static bool get isConnected =>
      _connection?.state == HubConnectionState.Connected;

  /// Kanalı açar. Çağrı tekrarlanabilir: bağlantı zaten varsa hiçbir şey yapmaz.
  ///
  /// Hata YUTULUYOR ve bu kasıtlı: bağlantı kurulamaması bir kullanım engeli değil,
  /// yalnızca bildirimlerin gelmemesi demek.
  static Future<void> connect() async {
    // KAPALI BAĞLANTI YENİDEN KURULUYOR.
    //
    // Eskiden koşul yalnız `_connection != null` idi ve bu kalıcı bir sessizlik üretiyordu:
    // `withAutomaticReconnect` varsayılan olarak ~30 saniye boyunca dört deneme yapıp pes
    // eder, ama `_connection` null OLMADIĞI için sonraki bütün `connect()` çağrıları
    // hiçbir şey yapmadan dönerdi. 40 saniyelik bir ağ kopması (Wi-Fi geçişi, uyku)
    // sonrasında kullanıcı ekranlar arasında gezinse bile OTURUM BOYUNCA hiçbir iş
    // bildirimi ve hiçbir izleyici uyarısı daha gelmiyordu — kart "okunuyor"da kalıyor,
    // kullanıcı ancak sayfayı yenilerse öğreniyordu.
    final state = _connection?.state;

    if (_connection != null &&
        state != HubConnectionState.Disconnected &&
        state != null) {
      return;
    }

    if (_connection != null) {
      // Ölü bağlantı bırakılıyor; aşağıda yenisi kuruluyor.
      _connection = null;
    }

    final token = await ApiService.hubToken();
    if (token == null) return;

    // Token adres satırında taşınıyor: tarayıcı, WebSocket el sıkışmasını başlatan
    // istekte özel başlık (Authorization) göndermeye izin vermiyor. Sunucu bu kabulü
    // yalnız /hubs yolu için açtı.
    final connection = HubConnectionBuilder()
        .withUrl(
          '${ApiService.baseUrl}/hubs/jobs',
          options: HttpConnectionOptions(
            accessTokenFactory: () async => await ApiService.hubToken() ?? '',
          ),
        )
        .withAutomaticReconnect()
        .build();

    connection.on('jobStatus', (arguments) {
      if (arguments == null || arguments.isEmpty) return;

      final payload = arguments.first;
      if (payload is! Map) return;

      try {
        _controller.add(JobNotification.fromJson(Map<String, dynamic>.from(payload)));
      } catch (_) {
        // Bozuk bir bildirim yüzünden kanal kapanmamalı.
      }
    });

    connection.on('watchAlert', (arguments) {
      if (arguments == null || arguments.isEmpty) return;

      final payload = arguments.first;
      if (payload is! Map) return;

      try {
        _alerts.add(
            WatchAlertNotification.fromJson(Map<String, dynamic>.from(payload)));
      } catch (_) {
        // Bozuk bir bildirim yüzünden kanal kapanmamalı.
      }
    });

    // YENİDEN BAĞLANINCA KAÇIRILAN DURUM TELAFİ EDİLİYOR.
    //
    // Kanal bir kolaylık, doğruluk kaynağı değil — ama kopukluk penceresinde gönderilen
    // bildirimler tamamen kayboluyordu ve onları telafi eden hiçbir kanca yoktu. İş
    // kopukluk sırasında bitmişse kart "okunuyor"da kalıyordu. Ekranlar bu akışı dinleyip
    // durumlarını sunucudan tazeliyor.
    connection.onreconnected(({connectionId}) {
      _reconnected.add(null);
    });

    try {
      await connection.start();
      _connection = connection;
    } catch (_) {
      _connection = null;
    }
  }

  /// Oturum kapanırken çağrılır: bağlantı kullanıcının kimliğine bağlı.
  static Future<void> disconnect() async {
    final connection = _connection;
    _connection = null;
    if (connection != null) await connection.stop();
  }
}

/// Kanaldan gelen haber — sonucun KENDİSİ değil.
///
/// Sunucu bilinçli olarak yalnız "şu iş şu duruma geçti" diyor; tablo istendiğinde ayrı
/// bir istekle çekiliyor. Bir belgeden yüzlerce hücre çıkabildiği için, kullanıcı başka
/// ekranda olsa bile bu yükü açık soketten itmek gereksiz olurdu.
class JobNotification {
  final String jobId;
  final String kind;
  final String status;
  final String? datasetId;
  final String? fileName;
  final String? error;

  JobNotification({
    required this.jobId,
    required this.kind,
    required this.status,
    this.datasetId,
    this.fileName,
    this.error,
  });

  factory JobNotification.fromJson(Map<String, dynamic> j) => JobNotification(
        jobId: j['jobId'] as String,
        kind: j['kind'] as String,
        status: j['status'] as String,
        datasetId: j['datasetId'] as String?,
        fileName: j['fileName'] as String?,
        error: j['error'] as String?,
      );

  bool get isFinished => status == 'succeeded' || status == 'failed';
}

/// Kanaldan gelen izleyici uyarısı.
///
/// Ölçülen değer bildirimde TAŞINIYOR (iş bildiriminin aksine): tek bir sayı, ve
/// kullanıcı bildirime bakarken zaten onu merak ediyor. Belge işinde taşınmayan şey
/// yüzlerce hücreydi.
///
/// Bu bildirim de doğruluk kaynağı DEĞİL: kanal kapalıysa uyarı kaybolmaz, rozet
/// `ApiService.watchAlerts` ile veritabanından okunmaya devam eder.
class WatchAlertNotification {
  final String watchId;
  final String runId;
  final String title;

  /// "ok" | "breaching" | "broken".
  final String status;
  final double? value;
  final String? error;

  WatchAlertNotification({
    required this.watchId,
    required this.runId,
    required this.title,
    required this.status,
    this.value,
    this.error,
  });

  bool get isBroken => status == 'broken';

  factory WatchAlertNotification.fromJson(Map<String, dynamic> j) =>
      WatchAlertNotification(
        watchId: j['watchId'] as String,
        runId: j['runId'] as String,
        title: j['title'] as String? ?? '',
        status: j['status'] as String? ?? 'ok',
        value: (j['value'] as num?)?.toDouble(),
        error: j['error'] as String?,
      );
}
