import 'dart:convert';
import 'package:http/http.dart' as http;

// Backend'in DatasetResponse'una karşılık gelen basit model (C# record karşılığı).
class Dataset {
  final String id;
  final String name;
  final String? description;
  final int rowCount;

  Dataset({
    required this.id,
    required this.name,
    this.description,
    required this.rowCount,
  });

  // JSON → nesne. Java'daki Jackson/ObjectMapper elle yazımı gibi.
  factory Dataset.fromJson(Map<String, dynamic> j) => Dataset(
        id: j['id'] as String,
        name: j['name'] as String,
        description: j['description'] as String?,
        rowCount: j['rowCount'] as int,
      );
}

// Backend ile tüm HTTP iletişimi tek yerde. Statik: iskelet için basit; ileride
// gerçek bir state yönetimine (provider vb.) taşınabilir. C# HttpClient sarmalayıcısı gibi.
class ApiService {
  // Flutter web tarayıcıda çalışır; backend aynı makinede 5000 portunda dinler.
  static const String baseUrl = 'http://localhost:5000';

  // Giriş sonrası saklanan JWT (bellek içi — iskelet için yeterli, kalıcı depo sonra).
  static String? _token;

  static bool get isLoggedIn => _token != null;
  static void logout() => _token = null;

  // POST /api/auth/register — tenant + admin birlikte açılır, token döner.
  // slug istemiyoruz; sunucu firma adından otomatik türetir.
  static Future<void> register({
    required String tenantName,
    required String email,
    required String password,
  }) async {
    final res = await http.post(
      Uri.parse('$baseUrl/api/auth/register'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'tenantName': tenantName,
        'email': email,
        'password': password,
      }),
    );
    _storeToken(res);
  }

  // POST /api/auth/login — token döner. E-posta global benzersiz olduğundan
  // giriş için yalnızca e-posta + şifre yeterli.
  static Future<void> login(String email, String password) async {
    final res = await http.post(
      Uri.parse('$baseUrl/api/auth/login'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'email': email, 'password': password}),
    );
    _storeToken(res);
  }

  // GET /api/datasets — token ile korumalı; yalnız bu tenant'ın setleri (query filter).
  static Future<List<Dataset>> getDatasets() async {
    final res = await http.get(
      Uri.parse('$baseUrl/api/datasets'),
      headers: {'Authorization': 'Bearer $_token'},
    );
    if (res.statusCode == 200) {
      final list = jsonDecode(res.body) as List<dynamic>;
      return list
          .map((e) => Dataset.fromJson(e as Map<String, dynamic>))
          .toList();
    }
    throw ApiException(_message(res));
  }

  // Başarılı auth yanıtından token'ı çıkar ve sakla; değilse hata fırlat.
  static void _storeToken(http.Response res) {
    if (res.statusCode >= 200 && res.statusCode < 300) {
      _token = (jsonDecode(res.body) as Map<String, dynamic>)['token'] as String;
    } else {
      throw ApiException(_message(res));
    }
  }

  // Okunur hata mesajı çıkar. Üç biçimi de ele alır:
  //  - Doğrulama hatası (ValidationProblemDetails): { errors: { alan: [mesaj...] } }
  //  - ProblemDetails (dataset controller): { detail, title }
  //  - Auth endpoint'leri: { message }
  static String _message(http.Response res) {
    try {
      final j = jsonDecode(res.body) as Map<String, dynamic>;

      // Doğrulama hataları: generic "One or more validation errors" title'ı yerine
      // alan-bazlı gerçek mesajları göster.
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
