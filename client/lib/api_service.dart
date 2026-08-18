import 'dart:convert';
import 'dart:typed_data';
import 'package:http/http.dart' as http;
import 'platform/platform.dart';

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
  /// Gruplama anahtarlarının tamamı. Tek gruplamada tek eleman; gruplanmış çubuk
  /// grafikte iki ("şehir VE kategoriye göre" → keys[0]=şehir, keys[1]=kategori).
  final List<String?> keys;
  final double? value;
  final int count;

  AggBucket({this.keys = const [], this.value, required this.count});

  /// İlk anahtar. Tek gruplamayla çalışan çağıranlar bunu okumaya devam eder.
  String? get key => keys.isNotEmpty ? keys[0] : null;

  /// İkinci anahtar (seri adı) — yalnız iki kolonla gruplandığında dolu.
  String? get subKey => keys.length > 1 ? keys[1] : null;

  factory AggBucket.fromJson(Map<String, dynamic> j) => AggBucket(
        // Sunucu hem 'keys' listesini hem tek 'key' kısayolunu döndürüyor; liste
        // yoksa kısayoldan tek elemanlı listeye düşülür.
        keys: (j['keys'] as List<dynamic>?)?.map((e) => e as String?).toList() ??
            [j['key'] as String?],
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

  // İki depo. localStorage: sekme/tarayıcı kapansa da kalır ("oturumu açık tut").
  // sessionStorage: yenilemede kalır ama sekme kapanınca uçar (işaretsiz mod).
  // Somut karşılıkları platform/ altında; VM'de (testlerde) bellekte tutulur.
  static KeyValueStore get _local => localStore;
  static KeyValueStore get _session => sessionStore;

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

  // Uygulama açılışında çağrılır. Access hâlâ geçerliyse onu kullan; süresi geçmişse
  // refresh ile sessizce yenile.
  //
  // Dönen değer ÜÇ durumu ayırır. Önceden ikiye indirilmişti (girişli / girişsiz) ve
  // "sunucuya ulaşılamıyor" hali girişsiz sayılıyordu: sunucu bir dakika kapalı kalsa
  // kullanıcı giriş ekranına düşüyor, "oturumum niye kapandı" diyor ve şifresini
  // giriyordu — o istek de aynı kapalı sunucuya gittiği için işe yaramıyordu.
  static Future<SessionState> loadToken() async {
    final access = _readAccess();
    if (access != null && _isTokenValid(access)) {
      _token = access;
      _refreshToken = _readRefresh();
      return SessionState.signedIn;
    }

    final refresh = _readRefresh();
    if (refresh == null) {
      _clearTokens();
      return SessionState.signedOut;
    }

    switch (await _tryRefresh(refresh)) {
      case RefreshOutcome.success:
        return SessionState.signedIn;

      // Sunucu token'ı reddetti: gerçekten geçersiz (süresi dolmuş, iptal edilmiş,
      // şifre değişmiş). Oturumu kapatmak doğru.
      case RefreshOutcome.rejected:
        _clearTokens();
        return SessionState.signedOut;

      // Sunucuya ulaşılamadı. Token'lara DOKUNMUYORUZ ve kullanıcıyı giriş ekranına
      // ATMIYORUZ: oturumu bitmedi, sadece sunucu yok. Kesinti geçince yeniden
      // denemesi yeterli; şifre girmesi gerekmiyor.
      case RefreshOutcome.unreachable:
        return SessionState.serverUnreachable;
    }
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
  // Süren yenileme çağrısı. Aynı anda birden fazla istek yenilemeye kalkarsa hepsi
  // BU tek çağrıyı bekler.
  static Future<RefreshOutcome>? _refreshInFlight;

  // Sunucu refresh token'ı DÖNDÜRÜYOR: kullanılan token geçersizleşip yenisi veriliyor.
  // Ekran açılışında birkaç istek (model listesi, öneriler, sohbetler) aynı anda gidiyor;
  // her biri ayrı ayrı yenileme çağırsaydı ilki token'ı döndürür, diğerleri artık
  // geçersiz olan eski token'la 401 alırdı — yani oturum kendi kendini bozardı.
  // Tek uçuş (single-flight) bunu engelliyor.
  static Future<RefreshOutcome> _tryRefresh(String refresh) =>
      _refreshInFlight ??=
          _doRefresh(refresh).whenComplete(() => _refreshInFlight = null);

  static Future<RefreshOutcome> _doRefresh(String refresh) async {
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
        return RefreshOutcome.success;
      }

      // Sunucu cevap verdi ve KABUL ETMEDİ → token gerçekten geçersiz (süresi dolmuş,
      // iptal edilmiş ya da şifre değişmiş). Oturumu kapatmak doğru.
      return RefreshOutcome.rejected;
    } catch (_) {
      // Sunucuya ULAŞILAMADI (kapalı, ağ kesik, yeniden başlıyor). Token'ın geçerli olup
      // olmadığı hakkında hiçbir şey öğrenmedik — bu yüzden SİLMİYORUZ. Silseydik geçici
      // bir kesinti "oturumu açık tut" seçen kullanıcıyı kalıcı olarak dışarı atardı.
      return RefreshOutcome.unreachable;
    }
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
    /// İkinci gruplama kolonu (gruplanmış çubuk grafik). groupBy olmadan anlamsızdır.
    String? groupBy2,
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

    // groupBy tekrarlanabilir bir parametre: sunucu sırayla okur.
    add('groupBy', groupBy);
    if (groupBy != null) add('groupBy', groupBy2);
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

  // POST /api/ask — doğal dilde soru. Veri seti kimliği GÖNDERİLMEZ: hangi setlerin
  // kullanılacağına model, firmanın kataloğuna bakarak kendisi karar verir.
  static Future<AskResult> ask(String question,
      {String? model, String? conversationId}) async {
    await _ensureFreshToken();
    final res = await http.post(
      Uri.parse('$baseUrl/api/ask'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({
        'question': question,
        'model': ?model,
        'conversationId': ?conversationId,
      }),
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
    return AskResult.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  // GET /api/ask/conversations — kullanıcının kendi sohbetleri.
  static Future<List<ChatSummary>> conversations() async {
    await _ensureFreshToken();
    final res = await http.get(
      Uri.parse('$baseUrl/api/ask/conversations'),
      headers: _authHeader,
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
    return (jsonDecode(res.body) as List)
        .map((e) => ChatSummary.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  // GET /api/ask/conversations/{id} — sohbetin turları.
  static Future<ChatDetail> conversation(String id) async {
    await _ensureFreshToken();
    final res = await http.get(
      Uri.parse('$baseUrl/api/ask/conversations/$id'),
      headers: _authHeader,
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
    return ChatDetail.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  // DELETE /api/ask/conversations/{id}
  static Future<void> deleteConversation(String id) async {
    await _ensureFreshToken();
    final res = await http.delete(
      Uri.parse('$baseUrl/api/ask/conversations/$id'),
      headers: _authHeader,
    );
    if (res.statusCode != 204) throw ApiException(_message(res));
  }

  // GET /api/ask/models — Ollama'da kurulu modeller (seçici bunu okur).
  static Future<List<AiModel>> aiModels() async {
    await _ensureFreshToken();
    final res = await http.get(Uri.parse('$baseUrl/api/ask/models'), headers: _authHeader);
    if (res.statusCode != 200) throw ApiException(_message(res));
    return (jsonDecode(res.body) as List)
        .map((e) => AiModel.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  // GET /api/ask/suggestions — firmanın kendi verisine göre örnek sorular.
  static Future<AskSuggestions> askSuggestions() async {
    await _ensureFreshToken();
    final res =
        await http.get(Uri.parse('$baseUrl/api/ask/suggestions'), headers: _authHeader);
    if (res.statusCode != 200) throw ApiException(_message(res));
    return AskSuggestions.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  // GET /api/relations — veri setleri arasındaki tanımlı bağlar.
  static Future<List<DatasetRelation>> relations() async {
    await _ensureFreshToken();
    final res = await http.get(Uri.parse('$baseUrl/api/relations'), headers: _authHeader);
    if (res.statusCode != 200) throw ApiException(_message(res));
    return (jsonDecode(res.body) as List)
        .map((e) => DatasetRelation.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  // POST /api/relations — yeni bağ tanımla. Bu bağ olmadan iki set birleştirilemez:
  // sistem "Satislar.musteri_no = Musteriler.no" ilişkisini kendiliğinden bilemez.
  static Future<DatasetRelation> createRelation({
    required String fromDatasetId,
    required String fromColumn,
    required String toDatasetId,
    required String toColumn,
  }) async {
    await _ensureFreshToken();
    final res = await http.post(
      Uri.parse('$baseUrl/api/relations'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({
        'fromDatasetId': fromDatasetId,
        'fromColumn': fromColumn,
        'toDatasetId': toDatasetId,
        'toColumn': toColumn,
      }),
    );
    if (res.statusCode != 201) throw ApiException(_message(res));
    return DatasetRelation.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  // DELETE /api/relations/{id}
  static Future<void> deleteRelation(String id) async {
    await _ensureFreshToken();
    final res = await http.delete(
      Uri.parse('$baseUrl/api/relations/$id'),
      headers: _authHeader,
    );
    if (res.statusCode != 204) throw ApiException(_message(res));
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

  // --- belge / OCR ---------------------------------------------------------------------

  /// POST /api/datasets/{id}/document/extract — hedef şema BİLİNİYOR.
  ///
  /// Belge okuma 30-150 saniye sürdüğü için uç sonucu değil bir İŞ döndürür. Sonuç hazır
  /// olunca canlı kanaldan haber gelir (bkz. JobHub); tablo `getDocumentJob` ile alınır.
  static Future<DocumentJob> queueExtractDocument(
      String datasetId, List<int> bytes, String filename) async {
    final body = await _uploadDocument(
        '$baseUrl/api/datasets/$datasetId/document/extract', bytes, filename);
    return DocumentJob.fromJson(body);
  }

  /// POST /api/documents/discover — hedef şema YOK (keşif geçişi). Bu da kuyruğa girer.
  static Future<DocumentJob> queueDiscoverDocument(
      List<int> bytes, String filename) async {
    final body = await _uploadDocument('$baseUrl/api/documents/discover', bytes, filename);
    return DocumentJob.fromJson(body);
  }

  /// GET /api/jobs/{id} — işin durumu ve bittiyse sonucu.
  ///
  /// Canlı kanal varken bu çağrının gerekliliği: bildirim bir kolaylıktır, doğruluk
  /// kaynağı değil. Bağlantı kopmuşken iş bitmiş olabilir ya da kullanıcı ekranı yeni
  /// açmış olabilir; durumun kesin hâli her zaman buradan okunur.
  static Future<DocumentJob> getDocumentJob(String jobId) async {
    await _ensureFreshToken();
    final res = await http.get(Uri.parse('$baseUrl/api/jobs/$jobId'), headers: _authHeader);
    if (res.statusCode != 200) throw ApiException(_message(res));
    return DocumentJob.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  /// GET /api/jobs — kullanıcının son belge işleri (sonuç gövdesi HARİÇ).
  static Future<List<DocumentJob>> listDocumentJobs() async {
    await _ensureFreshToken();
    final res = await http.get(Uri.parse('$baseUrl/api/jobs'), headers: _authHeader);
    if (res.statusCode != 200) throw ApiException(_message(res));
    return (jsonDecode(res.body) as List)
        .map((e) => DocumentJob.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  /// GET /api/jobs/{id}/image — onay ekranının yan yana gösterdiği belge görüntüsü.
  ///
  /// Baytlar okunuyor, adres verilmiyor: uç kimlik istiyor ve `Image.network` başlık
  /// gönderemez. Onaydan sonra görüntü silindiği için null dönebilir — bu bir hata değil,
  /// "belge artık saklanmıyor" demektir.
  static Future<Uint8List?> getDocumentJobImage(String jobId) async {
    await _ensureFreshToken();
    final res =
        await http.get(Uri.parse('$baseUrl/api/jobs/$jobId/image'), headers: _authHeader);
    if (res.statusCode == 404) return null;
    if (res.statusCode != 200) throw ApiException(_message(res));
    return res.bodyBytes;
  }

  /// DELETE /api/jobs/{id} — işi ve saklanan belge görüntüsünü siler.
  ///
  /// Yanlış yüklenen bir belgenin tek çıkış yolu. Ekranı kapatmak işi bitirmez; bu uç
  /// olmadan iş kalıcı olarak "kontrol bekliyor" durumunda kalırdı.
  static Future<void> deleteDocumentJob(String jobId) async {
    await _ensureFreshToken();
    final res =
        await http.delete(Uri.parse('$baseUrl/api/jobs/$jobId'), headers: _authHeader);
    if (res.statusCode != 204) throw ApiException(_message(res));
  }

  /// POST /api/datasets/{id}/document/confirm — onaylanan tabloyu EKLER.
  /// Tek hücre bile uymuyorsa sunucu hiçbir şey yazmaz ve hata fırlar.
  ///
  /// [jobId] verilirse sunucu belge görüntüsünü siler: görüntü işin ömrüne bağlı bir ara
  /// üründü, kalıcı olan az önce yazılan satırlardır.
  ///
  /// [newColumns] kullanıcının sete EKLENMESİNİ istediği kolonlar. Gönderilen başlıklardan
  /// biri sette yoksa ve burada da geçmiyorsa sunucu isteği reddediyor — eşleşmeyen kolonun
  /// sessizce düştüğü eski davranış böyle kapatıldı.
  static Future<int> confirmDocument(
      String datasetId, List<String> columns, List<List<String>> rows,
      {String? jobId, List<String> newColumns = const []}) async {
    await _ensureFreshToken();
    final res = await http.post(
      Uri.parse('$baseUrl/api/datasets/$datasetId/document/confirm'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({
        'columns': columns,
        'rows': rows,
        'jobId': jobId,
        'newColumns': newColumns,
      }),
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
    return (jsonDecode(res.body) as Map<String, dynamic>)['savedRows'] as int;
  }

  /// POST /api/datasets/{id}/document/align — tabloyu bir setin şemasına hizalar.
  ///
  /// Kullanıcı onay ekranında hedefi değiştirince çağrılır; belge YENİDEN OKUNMAZ, yalnız
  /// kolon adları karşılaştırılır (milisaniyeler). Eşleme kuralı sunucuda duruyor, burada
  /// tekrarlanmıyor.
  static Future<DocumentAlignment> alignDocument(
      String datasetId, List<String> columns, List<List<String>> rows) async {
    await _ensureFreshToken();
    final res = await http.post(
      Uri.parse('$baseUrl/api/datasets/$datasetId/document/align'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({'columns': columns, 'rows': rows}),
    );
    if (res.statusCode != 200) throw ApiException(_message(res));
    return DocumentAlignment.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  /// Canlı bildirim kanalının kimliği. Hub bağlantısı token'ı adres satırında taşır
  /// (tarayıcı WebSocket el sıkışmasında özel başlığa izin vermez).
  static Future<String?> hubToken() async {
    await _ensureFreshToken();
    return _token;
  }

  // Belge görüntüsünü multipart olarak yükler. Yanıt 202: iş alındı, henüz bitmedi.
  static Future<Map<String, dynamic>> _uploadDocument(
      String url, List<int> bytes, String filename) async {
    await _ensureFreshToken();
    final req = http.MultipartRequest('POST', Uri.parse(url));
    req.headers['Authorization'] = 'Bearer $_token';
    req.files.add(http.MultipartFile.fromBytes('file', bytes, filename: filename));

    final res = await http.Response.fromStream(await req.send());
    if (res.statusCode != 202) throw ApiException(_message(res));
    return jsonDecode(res.body) as Map<String, dynamic>;
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

/// Oturum yenilemenin sonucu.
///
/// [rejected] ile [unreachable] ayrımı kritik: ilkinde token gerçekten geçersizdir ve
/// oturum kapatılmalıdır, ikincisinde ise token hakkında hiçbir şey bilmiyoruzdur.
/// İkisini aynı saymak, sunucunun bir anlık kesintisinde "oturumu açık tut" seçmiş
/// kullanıcıyı kalıcı olarak dışarı atardı.
enum RefreshOutcome { success, rejected, unreachable }

/// Açılışta oturumun durumu.
///
/// [serverUnreachable] ile [signedOut] ayrımı kullanıcı için kritik: ilkinde oturum
/// DURUYOR, sadece sunucuya ulaşılamıyor — kullanıcıyı giriş ekranına atmak ona
/// "oturumun bitti" demek olur, oysa bitmemiştir. Sifresini yeniden girmesi de işe
/// yaramaz, çünkü giriş isteği de aynı sunucuya gider.
enum SessionState { signedIn, signedOut, serverUnreachable }

// ---------------------------------------------------------------------------
// Doğal dilde sorgu (/api/ask)
// ---------------------------------------------------------------------------

/// Ollama'da kurulu bir model. Boyut ve parametre sayısı seçiciye gösterilir:
/// kullanıcı hangi modelin daha ağır (ve yavaş) olduğunu görmeden seçim yapamaz.
class AiModel {
  final String name;
  final int sizeBytes;
  final String? parameterSize;
  final String? quantization;
  final bool isDefault;

  AiModel({
    required this.name,
    required this.sizeBytes,
    this.parameterSize,
    this.quantization,
    required this.isDefault,
  });

  factory AiModel.fromJson(Map<String, dynamic> j) => AiModel(
        name: j['name'] as String,
        sizeBytes: (j['sizeBytes'] as num?)?.toInt() ?? 0,
        parameterSize: j['parameterSize'] as String?,
        quantization: j['quantization'] as String?,
        isDefault: j['isDefault'] as bool? ?? false,
      );

  String get sizeLabel => sizeBytes <= 0
      ? ''
      : '${(sizeBytes / (1024 * 1024 * 1024)).toStringAsFixed(1)} GB';

  /// Dar alanda (seçici düğmesi) gösterilecek kısa ad.
  ///
  /// Kendi ince ayarlı modellerimizin adı `veriyonetim-` önekiyle başlıyor
  /// ("veriyonetim-planlayici:7b-k2"). Önek her modelimizde aynı olduğu için ayırt edici
  /// bilgi taşımıyor, yalnız yer kaplıyor. Tam ad menüde ve ipucunda duruyor.
  String get shortName =>
      name.startsWith('veriyonetim-') ? name.substring('veriyonetim-'.length) : name;
}

/// Satır listesi sonucu. Kolon adları ayrı taşınır: birleştirilmiş sorguda sütunlar
/// birden çok veri setinden gelir ("Musteriler.sehir").
class AskRows {
  final List<String> columns;
  final List<List<String?>> rows;

  AskRows({required this.columns, required this.rows});

  factory AskRows.fromJson(Map<String, dynamic> j) => AskRows(
        columns: (j['columns'] as List).map((e) => e as String).toList(),
        rows: (j['rows'] as List)
            .map((r) => (r as List).map((c) => c as String?).toList())
            .toList(),
      );
}

/// Bir ölçüm tanımı (işlem + kolon) — çoklu metrik yanıtlarında başlık üretmek için.
class AskMetric {
  final String op;
  final String? column;

  AskMetric({required this.op, this.column});

  factory AskMetric.fromJson(Map<String, dynamic> j) =>
      AskMetric(op: j['op'] as String? ?? '', column: j['column'] as String?);

  static const _labels = {
    'count': 'Adet',
    'sum': 'Toplam',
    'avg': 'Ortalama',
    'min': 'En düşük',
    'max': 'En yüksek',
    'median': 'Medyan',
    'countDistinct': 'Farklı değer',
  };

  String get label {
    final name = _labels[op] ?? op;
    return column == null ? name : '$column $name';
  }
}

/// Özet (agregasyon) sonucu.
class AskAggregate {
  final List<String> groupBy;
  final List<AskMetric> metrics;
  final String? bucket;
  final List<AskBucket> buckets;

  AskAggregate({
    required this.groupBy,
    required this.metrics,
    this.bucket,
    required this.buckets,
  });

  factory AskAggregate.fromJson(Map<String, dynamic> j) => AskAggregate(
        groupBy: (j['groupBy'] as List? ?? []).map((e) => e as String).toList(),
        metrics: (j['metrics'] as List? ?? [])
            .map((e) => AskMetric.fromJson(e as Map<String, dynamic>))
            .toList(),
        bucket: j['bucket'] as String?,
        buckets: (j['buckets'] as List? ?? [])
            .map((e) => AskBucket.fromJson(e as Map<String, dynamic>))
            .toList(),
      );

  /// Gruplama yoksa tek satırlık bir sonuçtur (KPI); grafik yerine rakam gösterilir.
  bool get isSingleValue => groupBy.isEmpty;
}

/// Tek bir grup: anahtarlar (çoklu gruplama), değerler (çoklu ölçüm), satır sayısı, pay.
class AskBucket {
  final List<String?> keys;
  final List<double?> values;
  final int count;
  final double? share;

  AskBucket({
    required this.keys,
    required this.values,
    required this.count,
    this.share,
  });

  factory AskBucket.fromJson(Map<String, dynamic> j) => AskBucket(
        keys: (j['keys'] as List? ?? []).map((e) => e as String?).toList(),
        values:
            (j['values'] as List? ?? []).map((e) => (e as num?)?.toDouble()).toList(),
        count: (j['count'] as num?)?.toInt() ?? 0,
        share: (j['share'] as num?)?.toDouble(),
      );

  String get label => keys.where((k) => k != null).join(' · ');
}

/// Dönem karşılaştırma satırı. previous/delta null olabilir: o grup önceki dönemde
/// HİÇ yoksa fark hesaplanamaz (eksik dönem sıfır sayılmıyor).
class AskComparisonRow {
  final String? key;
  final double? current;
  final double? previous;
  final double? delta;
  final double? deltaPercent;

  AskComparisonRow({
    this.key,
    this.current,
    this.previous,
    this.delta,
    this.deltaPercent,
  });

  factory AskComparisonRow.fromJson(Map<String, dynamic> j) => AskComparisonRow(
        key: j['key'] as String?,
        current: (j['current'] as num?)?.toDouble(),
        previous: (j['previous'] as num?)?.toDouble(),
        delta: (j['delta'] as num?)?.toDouble(),
        deltaPercent: (j['deltaPercent'] as num?)?.toDouble(),
      );
}

class AskComparison {
  final String period;
  final String previous;
  final List<AskComparisonRow> rows;

  AskComparison({
    required this.period,
    required this.previous,
    required this.rows,
  });

  factory AskComparison.fromJson(Map<String, dynamic> j) => AskComparison(
        period: j['period'] as String? ?? '',
        previous: j['previous'] as String? ?? '',
        rows: (j['buckets'] as List? ?? [])
            .map((e) => AskComparisonRow.fromJson(e as Map<String, dynamic>))
            .toList(),
      );
}

/// Sorgunun tam yanıtı.
///
/// Sunucu üretilen SQL'i de döndürüyor ama ARAYÜZ ONU OKUMUYOR: iş kullanıcısı SQL
/// okumaz. Kullanıcıya dönük doğrulama aracı `summary` ("anladığım sorgu") satırıdır —
/// modelin soruyu yanlış anladığını oradan fark eder.
class AskResult {
  final String question;
  final String kind; // "rows" | "aggregate" | "unsupported"
  final String summary;
  final List<String> datasets;
  final String model;
  final String? reason;

  /// Yanıtın kaydedildiği sohbet. Sonraki soru bununla gönderilir ki aynı sohbete eklensin.
  final String? conversationId;

  final int planMs;
  final int queryMs;
  final AskRows? rows;
  final AskAggregate? aggregate;
  final AskComparison? comparison;

  AskResult({
    required this.question,
    required this.kind,
    required this.summary,
    required this.datasets,
    required this.model,
    this.reason,
    this.conversationId,
    required this.planMs,
    required this.queryMs,
    this.rows,
    this.aggregate,
    this.comparison,
  });

  bool get isUnsupported => kind == 'unsupported';

  factory AskResult.fromJson(Map<String, dynamic> j) => AskResult(
        question: j['question'] as String? ?? '',
        kind: j['kind'] as String? ?? '',
        summary: j['summary'] as String? ?? '',
        datasets: (j['datasets'] as List? ?? []).map((e) => e as String).toList(),
        model: j['model'] as String? ?? '',
        reason: j['reason'] as String?,
        conversationId: j['conversationId'] as String?,
        planMs: (j['planMs'] as num?)?.toInt() ?? 0,
        queryMs: (j['queryMs'] as num?)?.toInt() ?? 0,
        rows: j['rows'] == null
            ? null
            : AskRows.fromJson(j['rows'] as Map<String, dynamic>),
        aggregate: j['aggregate'] == null
            ? null
            : AskAggregate.fromJson(j['aggregate'] as Map<String, dynamic>),
        comparison: j['comparison'] == null
            ? null
            : AskComparison.fromJson(j['comparison'] as Map<String, dynamic>),
      );
}

/// Karşılama ekranındaki örnek sorular.
///
/// [ready] false ise üretim sunucuda sürüyor demektir — sorular modele yazdırılıyor ve
/// her biri gösterilmeden önce gerçekten çalıştırılıp doğrulanıyor. İstemci biraz sonra
/// tekrar sorar.
class AskSuggestions {
  final bool ready;
  final List<String> questions;

  AskSuggestions({required this.ready, required this.questions});

  factory AskSuggestions.fromJson(Map<String, dynamic> j) => AskSuggestions(
        ready: j['ready'] as bool? ?? false,
        questions:
            (j['questions'] as List? ?? []).map((e) => e as String).toList(),
      );
}

/// Sohbet listesindeki bir satır.
class ChatSummary {
  final String id;
  final String title;
  final DateTime updatedAt;
  final int messageCount;

  ChatSummary({
    required this.id,
    required this.title,
    required this.updatedAt,
    required this.messageCount,
  });

  factory ChatSummary.fromJson(Map<String, dynamic> j) => ChatSummary(
        id: j['id'] as String,
        title: j['title'] as String,
        updatedAt: DateTime.tryParse(j['updatedAt'] as String? ?? '') ?? DateTime.now(),
        messageCount: (j['messageCount'] as num?)?.toInt() ?? 0,
      );
}

/// Geçmiş bir sohbetteki tek tur: soru + o gün verilen yanıt.
class ChatTurn {
  final String question;
  final AskResult result;

  ChatTurn({required this.question, required this.result});

  factory ChatTurn.fromJson(Map<String, dynamic> j) => ChatTurn(
        question: j['question'] as String,
        result: AskResult.fromJson(j['response'] as Map<String, dynamic>),
      );
}

class ChatDetail {
  final String id;
  final String title;
  final List<ChatTurn> turns;

  ChatDetail({required this.id, required this.title, required this.turns});

  factory ChatDetail.fromJson(Map<String, dynamic> j) => ChatDetail(
        id: j['id'] as String,
        title: j['title'] as String,
        turns: (j['turns'] as List? ?? [])
            .map((e) => ChatTurn.fromJson(e as Map<String, dynamic>))
            .toList(),
      );
}

/// İki veri seti arasındaki tanımlı bağ.
class DatasetRelation {
  final String id;
  final String fromDatasetId;
  final String fromDatasetName;
  final String fromColumn;
  final String toDatasetId;
  final String toDatasetName;
  final String toColumn;

  /// Bağı sistem mi buldu, kullanıcı mı tanımladı. Arayüzde ayırt edilir ki yanlış
  /// bulunmuş bir bağ fark edilip silinebilsin.
  final bool isAutoDetected;

  DatasetRelation({
    required this.id,
    required this.fromDatasetId,
    required this.fromDatasetName,
    required this.fromColumn,
    required this.toDatasetId,
    required this.toDatasetName,
    required this.toColumn,
    required this.isAutoDetected,
  });

  factory DatasetRelation.fromJson(Map<String, dynamic> j) => DatasetRelation(
        id: j['id'] as String,
        fromDatasetId: j['fromDatasetId'] as String,
        fromDatasetName: j['fromDatasetName'] as String,
        fromColumn: j['fromColumn'] as String,
        toDatasetId: j['toDatasetId'] as String,
        toDatasetName: j['toDatasetName'] as String,
        toColumn: j['toColumn'] as String,
        isAutoDetected: j['isAutoDetected'] as bool? ?? false,
      );

  String get label => '$fromDatasetName.$fromColumn = $toDatasetName.$toColumn';
}

// --- belge / OCR -----------------------------------------------------------------------

/// Belgedeki bir hücrenin şemaya uymadığını söyler (sunucudaki RowError'ın karşılığı).
/// Satır numarası 1 tabanlıdır ve başlık sayılmaz.
class DocumentCellError {
  final int row;
  final String column;
  final String? value;
  final String expectedType;

  DocumentCellError({
    required this.row,
    required this.column,
    this.value,
    required this.expectedType,
  });

  factory DocumentCellError.fromJson(Map<String, dynamic> j) => DocumentCellError(
        row: j['row'] as int,
        column: j['column'] as String,
        value: j['value'] as String?,
        expectedType: j['expectedType'] as String,
      );
}

/// Belgeden çıkarılan tablonun ÖNİZLEMESİ — sunucu hiçbir şey kaydetmedi.
///
/// `suspect` ve `warnings` bilerek taşınıyor: modelin yanıldığı durumu kullanıcının
/// fark etmesinin tek yolu bunları göstermek. Gizlenirse yarım okunmuş bir belge,
/// tam okunmuş gibi görünür.
/// Kuyruğa alınmış bir belge işi.
///
/// Belge okuma dakikalar sürebildiği için istemci artık sonucu değil bu kaydı bekliyor:
/// yükleme anında `queued` gelir, model çalışırken `running` olur, sonunda `succeeded`
/// ya da `failed`. Kullanıcı bu süre boyunca başka ekranlara geçebilir.
class DocumentJob {
  final String id;
  final String kind; // 'extract' | 'discover'
  final String status; // 'queued' | 'running' | 'succeeded' | 'failed'
  final String? datasetId;
  final String? fileName;
  final String? error;
  final DateTime createdAt;
  final DateTime? completedAt;

  /// Dolu ise bu belgeden çıkan satırlar zaten kaydedilmiş. Ekran ikinci kaydetmeyi
  /// kapatıyor: iş listesi kalıcı olduğu için kullanıcı eski bir işi açabiliyor ve
  /// tekrar kaydetmek aynı satırları sete ikinci kez eklerdi.
  final DateTime? confirmedAt;

  /// Sonuç gövdesi — yalnız bittiğinde ve tek iş sorgulandığında dolu (liste ucu taşımaz).
  final Map<String, dynamic>? result;

  DocumentJob({
    required this.id,
    required this.kind,
    required this.status,
    required this.createdAt,
    this.datasetId,
    this.fileName,
    this.error,
    this.completedAt,
    this.confirmedAt,
    this.result,
  });

  factory DocumentJob.fromJson(Map<String, dynamic> j) => DocumentJob(
        id: j['id'] as String,
        kind: j['kind'] as String,
        status: j['status'] as String,
        datasetId: j['datasetId'] as String?,
        fileName: j['fileName'] as String?,
        error: j['error'] as String?,
        createdAt: DateTime.parse(j['createdAt'] as String),
        completedAt: j['completedAt'] == null
            ? null
            : DateTime.parse(j['completedAt'] as String),
        confirmedAt: j['confirmedAt'] == null
            ? null
            : DateTime.parse(j['confirmedAt'] as String),
        result: j['result'] as Map<String, dynamic>?,
      );

  bool get isRunning => status == 'queued' || status == 'running';
  bool get isFinished => status == 'succeeded' || status == 'failed';
  bool get isDiscovery => kind == 'discover';
  bool get isConfirmed => confirmedAt != null;

  /// Sonucu ekranın beklediği biçime çevirir. İki geçiş iki farklı gövde döndürdüğü için
  /// ayrıştırıcı işin türüne bakılarak seçiliyor.
  DocumentExtraction? get extraction {
    final body = result;
    if (body == null) return null;
    return isDiscovery
        ? DocumentExtraction.fromDiscovery(body)
        : DocumentExtraction.fromExtract(body);
  }

  /// Kullanıcıya gösterilecek durum metni.
  String get statusLabel => switch (status) {
        'queued' => 'Sırada',
        'running' => 'Okunuyor',
        'succeeded' => isConfirmed ? 'Kaydedildi' : 'Hazır',
        'failed' => 'Başarısız',
        _ => status,
      };
}

class DocumentExtraction {
  final List<String> columns;
  final List<List<String>> rows;
  final List<DocumentCellError> errors;
  final List<String> warnings;
  final bool suspect;
  final String model;
  final int durationMs;

  /// Yalnız keşif geçişinde dolu.
  final String? documentType;
  final List<DatasetSuggestion> matches;
  final String suggestedName;

  /// Kolon tipleri — keşifte değerlerden algılandı, şemalı geçişte boş kalır.
  final List<SchemaColumn> detectedColumns;

  /// Şemalı geçişte hedef setin kolonlarıyla kurulan eşleme. Onay ekranı kolon
  /// başlıklarını bununla açıyor; keşif geçişinde hedef henüz yok, null kalır.
  final DocumentAlignment? alignment;

  DocumentExtraction({
    required this.columns,
    required this.rows,
    required this.errors,
    required this.warnings,
    required this.suspect,
    required this.model,
    required this.durationMs,
    this.documentType,
    this.matches = const [],
    this.suggestedName = '',
    this.detectedColumns = const [],
    this.alignment,
  });

  /// `POST /document/extract` yanıtı: şema biliniyor, kolonlar düz metin listesi.
  factory DocumentExtraction.fromExtract(Map<String, dynamic> j) => DocumentExtraction(
        columns: (j['columns'] as List).map((e) => e as String).toList(),
        rows: _rows(j['rows']),
        errors: (j['errors'] as List? ?? [])
            .map((e) => DocumentCellError.fromJson(e as Map<String, dynamic>))
            .toList(),
        warnings: (j['warnings'] as List? ?? []).map((e) => e as String).toList(),
        suspect: j['suspect'] as bool? ?? false,
        model: j['model'] as String? ?? '',
        durationMs: j['durationMs'] as int? ?? 0,
        alignment: j['alignment'] == null
            ? null
            : DocumentAlignment.fromJson(j['alignment'] as Map<String, dynamic>),
      );

  /// `POST /documents/discover` yanıtı: kolonlar tipleriyle gelir, üstüne set önerileri.
  factory DocumentExtraction.fromDiscovery(Map<String, dynamic> j) {
    final detected = (j['columns'] as List)
        .map((e) => SchemaColumn.fromJson({...e as Map<String, dynamic>, 'ordinal': 0}))
        .toList();

    return DocumentExtraction(
      columns: detected.map((c) => c.name).toList(),
      detectedColumns: detected,
      rows: _rows(j['rows']),
      errors: const [],
      warnings: (j['warnings'] as List? ?? []).map((e) => e as String).toList(),
      suspect: j['suspect'] as bool? ?? false,
      model: j['model'] as String? ?? '',
      durationMs: j['durationMs'] as int? ?? 0,
      documentType: j['documentType'] as String?,
      matches: (j['matches'] as List? ?? [])
          .map((e) => DatasetSuggestion.fromJson(e as Map<String, dynamic>))
          .toList(),
      suggestedName: j['suggestedName'] as String? ?? 'Belgeden gelen veriler',
    );
  }

  static List<List<String>> _rows(dynamic raw) => (raw as List)
      .map((r) => (r as List).map((c) => (c as String?) ?? '').toList())
      .toList();

  /// (satır, kolon) → hata. Onay ekranı hücreyi bununla işaretler.
  Map<String, DocumentCellError> get errorIndex => {
        for (final e in errors) '${e.row - 1}:${e.column}': e,
      };
}

/// Keşif geçişinin "bu belge şu veri setine ait olabilir" önerisi.
class DatasetSuggestion {
  final String datasetId;
  final String name;
  final double score;
  final List<ColumnMapping> mappings;
  final List<String> missingColumns;
  final List<String> extraColumns;

  DatasetSuggestion({
    required this.datasetId,
    required this.name,
    required this.score,
    required this.mappings,
    required this.missingColumns,
    required this.extraColumns,
  });

  factory DatasetSuggestion.fromJson(Map<String, dynamic> j) => DatasetSuggestion(
        datasetId: j['datasetId'] as String,
        name: j['name'] as String,
        score: (j['score'] as num).toDouble(),
        mappings: (j['mappings'] as List? ?? [])
            .map((e) => ColumnMapping.fromJson(e as Map<String, dynamic>))
            .toList(),
        missingColumns:
            (j['missingColumns'] as List? ?? []).map((e) => e as String).toList(),
        extraColumns:
            (j['extraColumns'] as List? ?? []).map((e) => e as String).toList(),
      );

  /// Yüzde olarak benzerlik — kullanıcıya 0-1 arası ondalık göstermek anlamsız.
  int get percent => (score * 100).round();
}

/// Belgeden çıkan kolonların BELİRLİ bir setin şemasına hizalanması.
///
/// [DatasetSuggestion] ile aynı bilgiyi taşır ama farklı bir soruya cevap verir: o "hangi
/// set" der, bu "hangi kolon nereye" der. Hedefin kolonlarını da taşıması şart — onay
/// ekranı eşlemeyi ancak seçenekleri bilirse kullanıcıya düzelttirebilir.
class DocumentAlignment {
  final String datasetId;
  final String name;
  final List<SchemaColumn> targetColumns;
  final List<ColumnMapping> mappings;
  final List<String> missingColumns;
  final List<String> extraColumns;

  DocumentAlignment({
    required this.datasetId,
    required this.name,
    required this.targetColumns,
    required this.mappings,
    required this.missingColumns,
    required this.extraColumns,
  });

  factory DocumentAlignment.fromJson(Map<String, dynamic> j) {
    var ordinal = 0;

    return DocumentAlignment(
      datasetId: j['datasetId'] as String,
      name: j['name'] as String? ?? '',
      // Sıra sunucudan geldiği gibi korunuyor (ordinal'e göre okunmuştu); istemcide
      // yeniden numaralandırmak yalnız SchemaColumn'un alanını doldurmak için.
      targetColumns: (j['targetColumns'] as List? ?? [])
          .map((e) => SchemaColumn.fromJson(
              {...e as Map<String, dynamic>, 'ordinal': ordinal++}))
          .toList(),
      mappings: (j['mappings'] as List? ?? [])
          .map((e) => ColumnMapping.fromJson(e as Map<String, dynamic>))
          .toList(),
      missingColumns:
          (j['missingColumns'] as List? ?? []).map((e) => e as String).toList(),
      extraColumns: (j['extraColumns'] as List? ?? []).map((e) => e as String).toList(),
    );
  }

  /// belge kolonu → set kolonu. Onay ekranı eşlemeyi bununla kuruyor.
  Map<String, ColumnMapping> get byDiscovered => {
        for (final m in mappings) m.discovered: m,
      };
}

/// Belgedeki bir kolonun hedef setteki karşılığı.
class ColumnMapping {
  final String discovered;
  final String target;
  final bool typeConflict;

  ColumnMapping({
    required this.discovered,
    required this.target,
    required this.typeConflict,
  });

  factory ColumnMapping.fromJson(Map<String, dynamic> j) => ColumnMapping(
        discovered: j['discovered'] as String,
        target: j['target'] as String,
        typeConflict: j['typeConflict'] as bool? ?? false,
      );
}
