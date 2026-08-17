import 'dart:async';

import 'package:signalr_netcore/signalr_client.dart';

import 'api_service.dart';

/// Belge işlerinin canlı bildirim kanalı.
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

  static bool get isConnected =>
      _connection?.state == HubConnectionState.Connected;

  /// Kanalı açar. Çağrı tekrarlanabilir: bağlantı zaten varsa hiçbir şey yapmaz.
  ///
  /// Hata YUTULUYOR ve bu kasıtlı: bağlantı kurulamaması bir kullanım engeli değil,
  /// yalnızca bildirimlerin gelmemesi demek.
  static Future<void> connect() async {
    if (_connection != null) return;

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
