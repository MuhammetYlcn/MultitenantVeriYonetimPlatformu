import 'dart:convert';
import 'package:http/http.dart' as http;
import 'package:web/web.dart' as web;

// Bir firmanın platform görünümü. DİKKAT: burada YALNIZCA metadata ve sayı var.
// Veri seti adı, kolon adı, satır içeriği, kullanıcı e-postası backend'de kasıtlı
// olarak hiç dönmüyor — panelin müşteri verisini gösterecek verisi yok.
class TenantSummary {
  final String id;
  final String name;
  final String slug;
  final bool isActive;
  final DateTime? createdAt;
  final DateTime? suspendedAt;
  final int userCount;
  final int datasetCount;
  final int rowCount;

  TenantSummary({
    required this.id,
    required this.name,
    required this.slug,
    required this.isActive,
    this.createdAt,
    this.suspendedAt,
    required this.userCount,
    required this.datasetCount,
    required this.rowCount,
  });

  factory TenantSummary.fromJson(Map<String, dynamic> j) => TenantSummary(
        id: j['id'] as String,
        name: j['name'] as String,
        slug: j['slug'] as String,
        isActive: j['isActive'] as bool,
        createdAt: DateTime.tryParse(j['createdAt'] as String? ?? ''),
        suspendedAt: DateTime.tryParse(j['suspendedAt'] as String? ?? ''),
        userCount: j['userCount'] as int,
        datasetCount: j['datasetCount'] as int,
        rowCount: j['rowCount'] as int,
      );
}

// Panelin üst şeridindeki toplamlar.
class PlatformStats {
  final int tenantCount;
  final int activeTenantCount;
  final int suspendedTenantCount;
  final int userCount;
  final int datasetCount;
  final int rowCount;

  PlatformStats({
    required this.tenantCount,
    required this.activeTenantCount,
    required this.suspendedTenantCount,
    required this.userCount,
    required this.datasetCount,
    required this.rowCount,
  });

  factory PlatformStats.fromJson(Map<String, dynamic> j) => PlatformStats(
        tenantCount: j['tenantCount'] as int,
        activeTenantCount: j['activeTenantCount'] as int,
        suspendedTenantCount: j['suspendedTenantCount'] as int,
        userCount: j['userCount'] as int,
        datasetCount: j['datasetCount'] as int,
        rowCount: j['rowCount'] as int,
      );
}

// Denetim kaydı satırı.
class AuditEntry {
  final String id;
  final String adminEmail;
  final String action;
  final String? tenantName;
  final DateTime? createdAt;

  AuditEntry({
    required this.id,
    required this.adminEmail,
    required this.action,
    this.tenantName,
    this.createdAt,
  });

  factory AuditEntry.fromJson(Map<String, dynamic> j) => AuditEntry(
        id: j['id'] as String,
        adminEmail: j['platformAdminEmail'] as String,
        action: j['action'] as String,
        tenantName: j['targetTenantName'] as String?,
        createdAt: DateTime.tryParse(j['createdAt'] as String? ?? ''),
      );
}

// İşlem adlarının arayüzde görünen Türkçe karşılıkları (kodda İngilizce kalır).
const Map<String, String> actionLabels = {
  'PlatformLogin': 'Panele giriş',
  'PlatformPasswordChanged': 'Şifre değiştirildi',
  'TenantSuspended': 'Firma askıya alındı',
  'TenantActivated': 'Firma etkinleştirildi',
};

/// Platform panelinin backend iletişimi. Müşteri uygulamasının ApiService'inden
/// AYRI bir dosya ve ayrı bir uygulamadır — panelin kodu müşteri tarayıcısına
/// hiç inmez.
///
/// Oturum: platform token'ının refresh'i YOKTUR (bilinçli — en yetkili kimliğe
/// en kısa tasma). Süresi dolunca giriş ekranına dönülür.
class PlatformApi {
  static const String baseUrl = 'http://localhost:5000';

  static String? _token;
  static String? _email;

  // Ayrı porttan servis edildiği için depo zaten müşteri uygulamasından yalıtık;
  // anahtar adı yine de ayrı tutulur ki aynı origin'de çalıştırılsa da karışmasın.
  static const String _tokenKey = 'platform_jwt';
  static const String _emailKey = 'platform_email';

  static web.Storage get _store => web.window.sessionStorage;

  static bool get isLoggedIn => _token != null;
  static String? get email => _email;

  static Map<String, String> get _authHeader => {'Authorization': 'Bearer $_token'};

  /// Açılışta çağrılır: depodaki token hâlâ geçerliyse oturum sürüyordur.
  static void loadSession() {
    final token = _store.getItem(_tokenKey);
    if (token != null && _isTokenValid(token)) {
      _token = token;
      _email = _store.getItem(_emailKey);
    } else {
      logout();
    }
  }

  static void logout() {
    _store.removeItem(_tokenKey);
    _store.removeItem(_emailKey);
    _token = null;
    _email = null;
  }

  /// JWT'nin 'exp' alanını sunucuya sormadan çözüp süresini kontrol eder.
  static bool _isTokenValid(String token) {
    try {
      final parts = token.split('.');
      if (parts.length != 3) return false;
      final payload = jsonDecode(
              utf8.decode(base64Url.decode(base64Url.normalize(parts[1]))))
          as Map<String, dynamic>;
      final exp = payload['exp'] as int?;
      if (exp == null) return false;
      final expiry = DateTime.fromMillisecondsSinceEpoch(exp * 1000, isUtc: true);
      return DateTime.now().toUtc().isBefore(expiry.subtract(const Duration(seconds: 10)));
    } catch (_) {
      return false;
    }
  }

  // POST /api/platform/auth/login
  static Future<void> login(String email, String password) async {
    final res = await http.post(
      Uri.parse('$baseUrl/api/platform/auth/login'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'email': email, 'password': password}),
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
    _storeSession(res);
  }

  // POST /api/platform/auth/change-password — ayarlardaki (env) tohum şifreyi
  // değiştirip diskte açık şifre bırakmamak için.
  static Future<void> changePassword(String current, String next) async {
    final res = await http.post(
      Uri.parse('$baseUrl/api/platform/auth/change-password'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({'currentPassword': current, 'newPassword': next}),
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
    // Sunucu yeni token döner → oturum kesilmeden devam eder.
    _storeSession(res);
  }

  // GET /api/platform/tenants
  static Future<List<TenantSummary>> getTenants() async {
    final res = await _get('/api/platform/tenants');
    return (jsonDecode(res.body) as List<dynamic>)
        .map((e) => TenantSummary.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  // GET /api/platform/stats
  static Future<PlatformStats> getStats() async {
    final res = await _get('/api/platform/stats');
    return PlatformStats.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  // GET /api/platform/audit-log
  static Future<List<AuditEntry>> getAuditLog({int limit = 30}) async {
    final res = await _get('/api/platform/audit-log?limit=$limit');
    return (jsonDecode(res.body) as List<dynamic>)
        .map((e) => AuditEntry.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  // PUT /api/platform/tenants/{id}/status — askıya al / etkinleştir.
  static Future<void> setTenantStatus(String tenantId, bool isActive) async {
    final res = await http.put(
      Uri.parse('$baseUrl/api/platform/tenants/$tenantId/status'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({'isActive': isActive}),
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
  }

  static Future<http.Response> _get(String path) async {
    final res = await http.get(Uri.parse('$baseUrl$path'), headers: _authHeader);
    if (res.statusCode == 200) return res;
    // Token süresi dolduysa oturumu düşür; arayüz giriş ekranına yönlendirir.
    if (res.statusCode == 401) {
      logout();
      throw ApiException('Oturum süresi doldu, yeniden giriş yapın.');
    }
    throw ApiException(_message(res));
  }

  static void _storeSession(http.Response res) {
    final j = jsonDecode(res.body) as Map<String, dynamic>;
    _token = j['token'] as String;
    _email = j['email'] as String;
    _store.setItem(_tokenKey, _token!);
    _store.setItem(_emailKey, _email!);
  }

  // Backend'in üç hata biçimini de okunur mesaja çevirir (client'takiyle aynı desen).
  static String _message(http.Response res) {
    try {
      final j = jsonDecode(res.body) as Map<String, dynamic>;

      final errors = j['errors'];
      if (errors is Map && errors.isNotEmpty) {
        final msgs = <String>[];
        for (final value in errors.values) {
          if (value is List) msgs.addAll(value.map((e) => e.toString()));
        }
        if (msgs.isNotEmpty) return msgs.join('\n');
      }

      return (j['detail'] ?? j['title'] ?? j['message'] ?? 'Hata: ${res.statusCode}')
          as String;
    } catch (_) {
      return 'Hata: ${res.statusCode}';
    }
  }
}

class ApiException implements Exception {
  final String message;
  ApiException(this.message);
  @override
  String toString() => message;
}
