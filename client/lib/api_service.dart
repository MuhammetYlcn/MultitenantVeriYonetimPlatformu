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

// Kaydedilmiş şema kolonu (ad + tip + sıra).
class SchemaColumn {
  final String name;
  final String type; // "text" | "number" | "date"
  final int ordinal;

  SchemaColumn({required this.name, required this.type, required this.ordinal});

  factory SchemaColumn.fromJson(Map<String, dynamic> j) => SchemaColumn(
        name: j['name'] as String,
        type: j['type'] as String,
        ordinal: j['ordinal'] as int,
      );
}

// Bir agregasyon grubu: anahtar (grup değeri, null=genel), değer, grup büyüklüğü.
class AggBucket {
  final String? key;
  final double? value;
  final int count;

  AggBucket({this.key, this.value, required this.count});

  factory AggBucket.fromJson(Map<String, dynamic> j) => AggBucket(
        key: j['key'] as String?,
        value: (j['value'] as num?)?.toDouble(),
        count: j['count'] as int,
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

  static Map<String, String> get _authHeader => {'Authorization': 'Bearer $_token'};

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

  // GET /api/datasets/{id}/schema — kaydedilmiş kolon tanımları.
  static Future<List<SchemaColumn>> getSchema(String datasetId) async {
    final res = await http.get(
      Uri.parse('$baseUrl/api/datasets/$datasetId/schema'),
      headers: _authHeader,
    );
    if (res.statusCode == 200) {
      final cols = (jsonDecode(res.body) as Map<String, dynamic>)['columns'] as List<dynamic>;
      return cols.map((c) => SchemaColumn.fromJson(c as Map<String, dynamic>)).toList();
    }
    throw ApiException(_message(res));
  }

  // GET /api/datasets/{id}/aggregate — gruplama/özet. groupBy null ise gruplamasız genel toplam.
  static Future<List<AggBucket>> aggregate(
    String datasetId, {
    String? groupBy,
    required String op,
    String? metric,
    String? bucket,
    String? sort,
    String? dir,
    int? limit,
    List<String> filters = const [],
  }) async {
    final qp = <String>['op=${Uri.encodeQueryComponent(op)}'];
    void add(String k, String? v) {
      if (v != null) qp.add('$k=${Uri.encodeQueryComponent(v)}');
    }

    add('groupBy', groupBy);
    add('metric', metric);
    add('bucket', bucket);
    add('sort', sort);
    add('dir', dir);
    if (limit != null) qp.add('limit=$limit');
    for (final f in filters) {
      qp.add('filter=${Uri.encodeQueryComponent(f)}');
    }

    final res = await http.get(
      Uri.parse('$baseUrl/api/datasets/$datasetId/aggregate?${qp.join('&')}'),
      headers: _authHeader,
    );
    if (res.statusCode == 200) {
      final buckets = (jsonDecode(res.body) as Map<String, dynamic>)['buckets'] as List<dynamic>;
      return buckets.map((b) => AggBucket.fromJson(b as Map<String, dynamic>)).toList();
    }
    throw ApiException(_message(res));
  }

  // Örnek bir veri seti oluşturup şema + satırları yükler (dashboard'u denemek için pratik
  // yol; gerçek CSV yükleme sonraki adımda file_picker ile eklenecek).
  static Future<void> seedSampleDataset() async {
    final id = await _createDataset('Örnek Satışlar');
    const csv = 'ad,sehir,tutar,tarih\n'
        'Ali,Ankara,1200,2026-01-10\n'
        'Ayse,Izmir,800,2026-01-22\n'
        'Veli,Ankara,1500,2026-02-05\n'
        'Cem,Bursa,600,2026-02-18\n'
        'Deniz,Izmir,2100,2026-03-03\n'
        'Ece,Ankara,900,2026-03-20\n';
    await _uploadCsv(id, 'schema', csv);
    await _uploadCsv(id, 'rows', csv);
  }

  // POST /api/datasets — yeni set, id döner.
  static Future<String> _createDataset(String name) async {
    final res = await http.post(
      Uri.parse('$baseUrl/api/datasets'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({'name': name, 'description': null}),
    );
    if (res.statusCode == 201) {
      return (jsonDecode(res.body) as Map<String, dynamic>)['id'] as String;
    }
    throw ApiException(_message(res));
  }

  // CSV içeriğini multipart/form-data olarak /{id}/schema veya /{id}/rows'a yükler.
  static Future<void> _uploadCsv(String datasetId, String endpoint, String csv) async {
    final req = http.MultipartRequest(
        'POST', Uri.parse('$baseUrl/api/datasets/$datasetId/$endpoint'));
    req.headers['Authorization'] = 'Bearer $_token';
    // İçerik tipi değil dosya uzantısı kontrol edildiğinden filename yeterli.
    req.files.add(http.MultipartFile.fromString('file', csv, filename: 'ornek.csv'));
    final res = await http.Response.fromStream(await req.send());
    if (res.statusCode != 200) throw ApiException(_message(res));
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
