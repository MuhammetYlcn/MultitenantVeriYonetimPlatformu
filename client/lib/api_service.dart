import 'dart:convert';
import 'package:http/http.dart' as http;
import 'package:web/web.dart' as web;

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

// Bir veri satırı: kimlik + kolon adı→değer haritası (backend'in JSONB 'data' alanı).
class RowItem {
  final String id;
  final Map<String, dynamic> data;

  RowItem({required this.id, required this.data});

  factory RowItem.fromJson(Map<String, dynamic> j) => RowItem(
        id: j['id'] as String,
        data: (j['data'] as Map).cast<String, dynamic>(),
      );
}

// Sayfalanmış satır listesi (toplam + sayfa metadata'sı ile).
class RowPage {
  final int page;
  final int pageSize;
  final int total;
  final int totalPages;
  final List<RowItem> rows;

  RowPage({
    required this.page,
    required this.pageSize,
    required this.total,
    required this.totalPages,
    required this.rows,
  });

  factory RowPage.fromJson(Map<String, dynamic> j) => RowPage(
        page: j['page'] as int,
        pageSize: j['pageSize'] as int,
        total: j['total'] as int,
        totalPages: j['totalPages'] as int,
        rows: (j['rows'] as List)
            .map((e) => RowItem.fromJson(e as Map<String, dynamic>))
            .toList(),
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

// Tenant'ın bir kullanıcısı (GET /api/users yanıtı).
class AppUser {
  final String id;
  final String email;
  final String role; // "Viewer" | "Editor" | "Admin"
  final DateTime? createdAt;

  AppUser({
    required this.id,
    required this.email,
    required this.role,
    this.createdAt,
  });

  factory AppUser.fromJson(Map<String, dynamic> j) => AppUser(
        id: j['id'] as String,
        email: j['email'] as String,
        role: j['role'] as String,
        createdAt: DateTime.tryParse(j['createdAt'] as String? ?? ''),
      );
}

// Davet / şifre sıfırlama bağlantısı. Ham token YALNIZCA burada, bir kez görünür;
// sunucuda yalnız SHA-256 özeti saklanır.
class AccountLink {
  final String token;
  final String email;
  final String? role;
  final DateTime? expiresAt;
  final String purpose; // "Invite" | "PasswordReset"

  AccountLink({
    required this.token,
    required this.email,
    this.role,
    this.expiresAt,
    required this.purpose,
  });

  factory AccountLink.fromJson(Map<String, dynamic> j) => AccountLink(
        token: j['token'] as String,
        email: j['email'] as String,
        role: j['role'] as String?,
        expiresAt: DateTime.tryParse(j['expiresAt'] as String? ?? ''),
        purpose: j['purpose'] as String,
      );

  // Kullanıcıya iletilecek tam adres. Flutter web varsayılan olarak hash yönlendirme
  // kullandığından yol '#/davet/<token>' biçiminde olur.
  String url(String origin) => '$origin/#/davet/$token';
}

// Bağlantı açıldığında ekranda ne yazacağını belirleyen bilgi.
class AccountLinkInfo {
  final String purpose;
  final String email;
  final String? role;
  final String tenantName;

  AccountLinkInfo({
    required this.purpose,
    required this.email,
    this.role,
    required this.tenantName,
  });

  bool get isInvite => purpose == 'Invite';

  factory AccountLinkInfo.fromJson(Map<String, dynamic> j) => AccountLinkInfo(
        purpose: j['purpose'] as String,
        email: j['email'] as String,
        role: j['role'] as String?,
        tenantName: j['tenantName'] as String,
      );
}

// Rollerin arayüzde görünen Türkçe adları (kodda İngilizce kalır).
const Map<String, String> roleLabels = {
  'Viewer': 'İzleyici',
  'Editor': 'Editör',
  'Admin': 'Yönetici',
};

const Map<String, String> roleDescriptions = {
  'Viewer': 'Yalnız görüntüler',
  'Editor': 'Veri ekler/düzenler',
  'Admin': 'Veri + kullanıcı yönetimi',
};

// Backend ile tüm HTTP iletişimi tek yerde. Statik: iskelet için basit; ileride
// gerçek bir state yönetimine (provider vb.) taşınabilir. C# HttpClient sarmalayıcısı gibi.
class ApiService {
  // Flutter web tarayıcıda çalışır; backend aynı makinede 5000 portunda dinler.
  static const String baseUrl = 'http://localhost:5000';

  // Access token (JWT, ~15 dk). Bellekte tutulur (hızlı erişim); kaynak doğruluk tarayıcı
  // deposudur. Refresh token (uzun ömür) access dolunca yenileme için saklanır.
  static String? _token;
  static String? _refreshToken;

  static const String _accessKey = 'jwt';
  static const String _refreshKey = 'refresh';

  // İki tarayıcı deposu. localStorage: sekme/tarayıcı kapansa da kalır ("oturumu açık tut").
  // sessionStorage: yenilemede kalır ama sekme kapanınca uçar (işaretsiz mod).
  static web.Storage get _local => web.window.localStorage;
  static web.Storage get _session => web.window.sessionStorage;

  static bool get isLoggedIn => _token != null;

  static Map<String, String> get _authHeader => {'Authorization': 'Bearer $_token'};

  // Giriş yapan kullanıcının rolü — token'ın payload'ından okunur (sunucuya sormadan).
  // Yalnız ARAYÜZ içindir: yetkisiz butonları gizlemek için. Gerçek koruma backend'de
  // ([Authorize(Roles=...)]) — istemcideki bu kontrol atlatılsa bile sunucu 403 döner.
  static String? get currentRole => _claim([
        'role',
        'http://schemas.microsoft.com/ws/2008/06/identity/claims/role',
      ]);

  static String? get currentUserId => _claim(['sub', 'nameid']);
  static String? get currentEmail => _claim(['email']);

  static bool get isAdmin => currentRole == 'Admin';
  // Editor ve Admin yazabilir; Viewer yalnız okur.
  static bool get canWrite => currentRole == 'Editor' || currentRole == 'Admin';

  // Token payload'ından ilk bulunan claim'i döndürür (claim adı sürüme göre kısa ad ya da
  // uzun Microsoft URI'si olabilir; ikisini de deneriz).
  static String? _claim(List<String> names) {
    final token = _token;
    if (token == null) return null;
    try {
      final parts = token.split('.');
      if (parts.length != 3) return null;
      final payload = jsonDecode(
              utf8.decode(base64Url.decode(base64Url.normalize(parts[1]))))
          as Map<String, dynamic>;
      for (final n in names) {
        final v = payload[n];
        if (v is String) return v;
        if (v is List && v.isNotEmpty) return v.first.toString();
      }
    } catch (_) {/* bozuk token → null */}
    return null;
  }

  // Token'ları seçilen depoya yaz + belleğe al; diğer depodaki kalıntıyı sil (mod değişebilir).
  static void _saveTokens(String access, String refresh, {required bool remember}) {
    final target = remember ? _local : _session;
    final other = remember ? _session : _local;
    target.setItem(_accessKey, access);
    target.setItem(_refreshKey, refresh);
    other.removeItem(_accessKey);
    other.removeItem(_refreshKey);
    _token = access;
    _refreshToken = refresh;
  }

  // Her iki depoyu ve belleği temizle.
  static void _clearTokens() {
    for (final s in [_local, _session]) {
      s.removeItem(_accessKey);
      s.removeItem(_refreshKey);
    }
    _token = null;
    _refreshToken = null;
  }

  static String? _readAccess() => _local.getItem(_accessKey) ?? _session.getItem(_accessKey);
  static String? _readRefresh() => _local.getItem(_refreshKey) ?? _session.getItem(_refreshKey);
  // Refresh'i localStorage'da bulduysak "oturumu açık tut" modundayız → yenilemede aynı moda yaz.
  static bool _isRemembered() => _local.getItem(_refreshKey) != null;

  // Uygulama açılışında çağrılır. Access hâlâ geçerliyse onu kullan; süresi geçmişse ama
  // refresh varsa sessizce yenile; ikisi de yoksa temizle (giriş ekranına düşülür).
  static Future<void> loadToken() async {
    final access = _readAccess();
    if (access != null && _isTokenValid(access)) {
      _token = access;
      _refreshToken = _readRefresh();
      return;
    }
    final refresh = _readRefresh();
    if (refresh != null && await _tryRefresh(refresh)) return;
    _clearTokens();
  }

  // Yetkili bir istekten ÖNCE çağrılır: access süresi dolduysa refresh ile yeniler (aktif
  // kullanımda 15 dk'da bir atılmamak için). Refresh yoksa dokunmaz; istek 401 alırsa çağıran görür.
  static Future<void> _ensureFreshToken() async {
    if (_token != null && _isTokenValid(_token!)) return;
    final refresh = _refreshToken ?? _readRefresh();
    if (refresh != null) await _tryRefresh(refresh);
  }

  // Refresh token ile /api/auth/refresh'e gider, yeni access+refresh alır. Backend rotation
  // uygular (yeni refresh gelir, eskisi geçersizleşir) → yeni refresh'i de saklamak ŞART.
  // Başarılıysa aynı modda (kalıcı/oturumluk) saklar ve true döner.
  static Future<bool> _tryRefresh(String refresh) async {
    try {
      final remember = _isRemembered();
      final res = await http.post(
        Uri.parse('$baseUrl/api/auth/refresh'),
        headers: {'Content-Type': 'application/json'},
        body: jsonEncode({'refreshToken': refresh}),
      );
      if (res.statusCode == 200) {
        final j = jsonDecode(res.body) as Map<String, dynamic>;
        _saveTokens(j['token'] as String, j['refreshToken'] as String, remember: remember);
        return true;
      }
    } catch (_) {/* ağ hatası vb. → yenileme başarısız */}
    return false;
  }

  // Çıkış: token'ları her yerden siler.
  static Future<void> logout() async => _clearTokens();

  // JWT'nin süresi geçmemişse true. Token 3 parça: header.payload.signature (noktayla ayrık).
  // Ortadaki payload base64 kodlu bir JSON'dur; içindeki 'exp' (Unix saniye) son geçerlilik
  // anını taşır. Bunu SUNUCUYA sormadan istemcide çözüp kontrol ederiz.
  static bool _isTokenValid(String token) {
    try {
      final parts = token.split('.');
      if (parts.length != 3) return false;
      // base64url uzunluğu 4'ün katı olmalı; normalize eksik '=' dolgusunu tamamlar.
      final payloadJson = utf8.decode(base64Url.decode(base64Url.normalize(parts[1])));
      final exp = (jsonDecode(payloadJson) as Map<String, dynamic>)['exp'] as int?;
      if (exp == null) return false;
      final expiry = DateTime.fromMillisecondsSinceEpoch(exp * 1000, isUtc: true);
      // 10 sn pay: tam sınırda "geçerli" deyip hemen 401 yememek için.
      return DateTime.now().toUtc().isBefore(expiry.subtract(const Duration(seconds: 10)));
    } catch (_) {
      return false; // bozuk / çözülemeyen token → geçersiz say.
    }
  }

  // POST /api/auth/register — tenant + admin birlikte açılır, token döner.
  // slug istemiyoruz; sunucu firma adından otomatik türetir.
  static Future<void> register({
    required String tenantName,
    required String email,
    required String password,
    required bool rememberMe,
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
    _storeAuth(res, rememberMe);
  }

  // POST /api/auth/login — token döner. E-posta global benzersiz olduğundan
  // giriş için yalnızca e-posta + şifre yeterli.
  static Future<void> login(String email, String password,
      {required bool rememberMe}) async {
    final res = await http.post(
      Uri.parse('$baseUrl/api/auth/login'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'email': email, 'password': password}),
    );
    _storeAuth(res, rememberMe);
  }

  // GET /api/users — bu tenant'ın kullanıcıları (izolasyonu global query filter sağlar).
  static Future<List<AppUser>> getUsers() async {
    await _ensureFreshToken();
    final res = await http.get(Uri.parse('$baseUrl/api/users'), headers: _authHeader);
    if (res.statusCode == 200) {
      return (jsonDecode(res.body) as List<dynamic>)
          .map((e) => AppUser.fromJson(e as Map<String, dynamic>))
          .toList();
    }
    throw ApiException(_message(res));
  }

  // POST /api/users/invite — kullanıcı DAVET eder (yalnız Admin). Şifre alanı YOK:
  // Admin başkasının şifresini bilmemeli. Dönen tek kullanımlık bağlantı kullanıcıya
  // iletilir, şifreyi o belirler.
  static Future<AccountLink> inviteUser(
      {required String email, required String role}) async {
    await _ensureFreshToken();
    final res = await http.post(
      Uri.parse('$baseUrl/api/users/invite'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({'email': email, 'role': role}),
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
    return AccountLink.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  // POST /api/users/{id}/reset-password — tek kullanımlık şifre sıfırlama bağlantısı
  // üretir (yalnız Admin). Admin yeni şifreyi görmez.
  static Future<AccountLink> createPasswordReset(String userId) async {
    await _ensureFreshToken();
    final res = await http.post(
      Uri.parse('$baseUrl/api/users/$userId/reset-password'),
      headers: _authHeader,
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
    return AccountLink.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  // POST /api/auth/change-password — kullanıcı kendi şifresini değiştirir.
  // Başarılı olduğunda sunucu tüm refresh token'ları iptal eder, o yüzden yerel
  // oturum da temizlenir: kullanıcı yeniden giriş yapar.
  static Future<void> changePassword(
      {required String currentPassword, required String newPassword}) async {
    await _ensureFreshToken();
    final res = await http.post(
      Uri.parse('$baseUrl/api/auth/change-password'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode(
          {'currentPassword': currentPassword, 'newPassword': newPassword}),
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
    _clearTokens();
  }

  // GET /api/invitations/{token} — bağlantının geçerliliği ve bağlamı (giriş GEREKMEZ:
  // davet edilen kişinin henüz hesabı yoktur). Token'ı harcamaz.
  static Future<AccountLinkInfo> inspectInvitation(String token) async {
    final res = await http.get(Uri.parse('$baseUrl/api/invitations/$token'));
    if (res.statusCode != 200) throw ApiException(_message(res));
    return AccountLinkInfo.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  // POST /api/invitations/{token}/accept — kullanıcı şifresini kendisi belirler.
  static Future<void> acceptInvitation(String token, String password) async {
    final res = await http.post(
      Uri.parse('$baseUrl/api/invitations/$token/accept'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'password': password}),
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
  }

  // PUT /api/users/{id}/role — rol değiştirme (yalnız Admin). Son yönetici düşürülmek
  // istenirse backend 409 döner; mesajı olduğu gibi kullanıcıya gösteririz.
  static Future<void> updateUserRole(String userId, String role) async {
    await _ensureFreshToken();
    final res = await http.put(
      Uri.parse('$baseUrl/api/users/$userId/role'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({'role': role}),
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
  }

  // GET /api/datasets — token ile korumalı; yalnız bu tenant'ın setleri (query filter).
  static Future<List<Dataset>> getDatasets() async {
    await _ensureFreshToken();
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

  // PUT /api/datasets/{id} — veri setini yeniden adlandır (açıklama korunur/verilebilir).
  static Future<void> renameDataset(String datasetId, String name,
      {String? description}) async {
    await _ensureFreshToken();
    final res = await http.put(
      Uri.parse('$baseUrl/api/datasets/$datasetId'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({'name': name, 'description': description}),
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
  }

  // DELETE /api/datasets/{id} — veri setini (kolonlar+satırlar cascade) siler.
  static Future<void> deleteDataset(String datasetId) async {
    await _ensureFreshToken();
    final res = await http.delete(
      Uri.parse('$baseUrl/api/datasets/$datasetId'),
      headers: _authHeader,
    );
    if (res.statusCode != 204) throw ApiException(_message(res));
  }

  // GET /api/datasets/{id}/schema — kaydedilmiş kolon tanımları.
  static Future<List<SchemaColumn>> getSchema(String datasetId) async {
    await _ensureFreshToken();
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

  // GET /api/datasets/{id}/rows — sayfalanmış ham satırlar (şemaya göre dinamik tablo için).
  static Future<RowPage> getRows(String datasetId,
      {int page = 1, int pageSize = 50}) async {
    await _ensureFreshToken();
    final res = await http.get(
      Uri.parse('$baseUrl/api/datasets/$datasetId/rows?page=$page&pageSize=$pageSize'),
      headers: _authHeader,
    );
    if (res.statusCode == 200) {
      return RowPage.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
    }
    throw ApiException(_message(res));
  }

  // POST /api/datasets/{id}/rows/add — tek satır ekler. Değerler metin gider; sunucu şemaya
  // göre tipli doğrular (number/date). Uymayan değer → ApiException (400 mesajı).
  static Future<void> addRow(String datasetId, Map<String, String> values) async {
    await _ensureFreshToken();
    final res = await http.post(
      Uri.parse('$baseUrl/api/datasets/$datasetId/rows/add'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({'values': values}),
    );
    if (res.statusCode != 201) throw ApiException(_message(res));
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
    await _ensureFreshToken();
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

  // Gerçek bir CSV/Excel dosyasını yükler: önce yeni veri seti oluşturur, sonra AYNI dosyayı
  // hem /schema (kolon+tip algılama) hem /rows (satırları içe aktarma) uçlarına gönderir.
  // Backend uzantıya (.csv/.xlsx) göre ayrıştırdığından gerçek dosya adı geçilir.
  static Future<void> uploadDataset({
    required String name,
    required List<int> bytes,
    required String filename,
  }) async {
    final id = await _createDataset(name);
    await _uploadBytes(id, 'schema', bytes, filename);
    await _uploadBytes(id, 'rows', bytes, filename);
  }

  // Örnek veri seti (dashboard'u hızlı denemek için). Gömülü CSV'yi byte'a çevirip gerçek
  // upload yoluyla gönderir — artık file_picker akışıyla birebir aynı kodu kullanır.
  static Future<void> seedSampleDataset() async {
    const csv = 'ad,sehir,tutar,tarih\n'
        'Ali,Ankara,1200,2026-01-10\n'
        'Ayse,Izmir,800,2026-01-22\n'
        'Veli,Ankara,1500,2026-02-05\n'
        'Cem,Bursa,600,2026-02-18\n'
        'Deniz,Izmir,2100,2026-03-03\n'
        'Ece,Ankara,900,2026-03-20\n';
    await uploadDataset(
        name: 'Örnek Satışlar', bytes: utf8.encode(csv), filename: 'ornek.csv');
  }

  // POST /api/datasets — yeni set, id döner.
  static Future<String> _createDataset(String name) async {
    await _ensureFreshToken();
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

  // Dosya byte'larını multipart/form-data olarak /{id}/schema veya /{id}/rows'a yükler.
  // Backend içerik tipine değil dosya uzantısına baktığından filename (uzantısıyla) önemli.
  static Future<void> _uploadBytes(
      String datasetId, String endpoint, List<int> bytes, String filename) async {
    await _ensureFreshToken();
    final req = http.MultipartRequest(
        'POST', Uri.parse('$baseUrl/api/datasets/$datasetId/$endpoint'));
    req.headers['Authorization'] = 'Bearer $_token';
    req.files.add(http.MultipartFile.fromBytes('file', bytes, filename: filename));
    final res = await http.Response.fromStream(await req.send());
    if (res.statusCode != 200) throw ApiException(_message(res));
  }

  // Başarılı auth yanıtından access + refresh token'ı çıkar ve seçilen moda göre sakla;
  // başarısızsa hata fırlat.
  static void _storeAuth(http.Response res, bool rememberMe) {
    if (res.statusCode >= 200 && res.statusCode < 300) {
      final j = jsonDecode(res.body) as Map<String, dynamic>;
      _saveTokens(j['token'] as String, j['refreshToken'] as String, remember: rememberMe);
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
