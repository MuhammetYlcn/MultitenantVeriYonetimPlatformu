using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Hubs;
using VeriYonetim.Api.Middleware;
using VeriYonetim.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Sırların denetimi — AÇILIŞTA, uygulama istek kabul etmeye başlamadan önce.
//
// Bu sırların hiçbiri depoda durmuyor: appsettings.Development.json .gitignore'da,
// yanında yalnızca yer tutucu değerler taşıyan .example dosyası duruyor; teslimde
// aynı değerler ortam değişkeniyle (ConnectionStrings__DefaultConnection, Jwt__Key…)
// veriliyor. Ama "depoda yok" tek başına yetmiyor: ayarı EKSİK bir kurulumun ne
// yapacağı da tanımlı olmalı.
//
// Eskiden tanımlı değildi. Jwt:Key okunurken "!" ile null olmadığı varsayılıyordu;
// anahtar verilmemiş bir kurulumda uygulama açılışta değil, İLK GİRİŞ DENEMESİNDE,
// hiçbir şey söylemeyen bir hatayla düşerdi. Daha kötüsü uzunluk denetimiydi: HS256
// anahtarı 32 bayttan kısaysa .NET zaten reddeder, ama kısa bir anahtar yazan kişi bunu
// ancak çalışma anında öğrenirdi.
//
// Kural: eksik ya da zayıf sır = AÇILMAYAN uygulama. Sessizce çalışan ama korumayan bir
// kurulumdan, hiç açılmayan ve sebebini söyleyen bir kurulum iyidir.
// ---------------------------------------------------------------------------
static string RequiredSetting(IConfiguration config, string key, string hint)
{
    // Örnek dosyalardaki bütün yer tutucular bu önekle başlıyor. Arama `Contains` ile
    // yapılıyor, `StartsWith` ile değil: bağlantı dizesinde yer tutucu değer başta değil
    // `...;Password=BURAYA-YEREL-DB-SIFRESI` gibi ortada duruyor.
    const string PlaceholderPrefix = "BURAYA-";

    var value = config[key];

    if (!string.IsNullOrWhiteSpace(value))
    {
        // YER TUTUCU REDDİ. Denetim "eksik" ve (aşağıda) "kısa" hâllerini yakalıyordu ama
        // yakalamak istediği ASIL senaryoyu kaçırıyordu: kurulumu yapan kişinin örnek
        // dosyayı kopyalayıp doldurmayı unutması. Depodaki yer tutucu 50 karakter, yani
        // 32 baytlık eşiği rahatça aşıyor ve uygulama hiç uyarmadan açılıyordu. O
        // durumda imzalama anahtarı herkese açık depoda YAZILI BİR SABİT olur: anahtarı
        // bilen kendi token'ını üretip `tenant_id`'yi istediği firmaya, rolü Admin'e
        // koyar — kilit, karma ve kiracı izolasyonu devreye bile girmez, çünkü kimlik
        // doğrulaması hiç yapılmaz. Eksik ayardan tehlikeli olan, doldurulmuş görünen ayar.
        if (value.Contains(PlaceholderPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"'{key}' hâlâ örnek dosyadaki yer tutucu değeri taşıyor. {hint} " +
                "Bu değer depoda yazılı olduğu için sır sayılmaz; gerçek bir değer üretin " +
                "(ör. `openssl rand -base64 48`).");

        return value;
    }

    throw new InvalidOperationException(
        $"Zorunlu ayar eksik: '{key}'. {hint} " +
        "Ayar dosyası için appsettings.Development.example.json'a bakın; teslimde " +
        $"karşılığı '{key.Replace(":", "__")}' ortam değişkenidir.");
}

RequiredSetting(builder.Configuration, "ConnectionStrings:DefaultConnection",
    "Veritabanı bağlantısı olmadan uygulama açılamaz.");

var jwtKey = RequiredSetting(builder.Configuration, "Jwt:Key",
    "Bu anahtar tüm oturum token'larını imzalar.");

// HS256 için anahtar en az 256 bit olmalı. Kısa anahtar yalnız "zayıf" değil, .NET
// tarafından da reddediliyor — burada yakalanmazsa hata ilk token üretiminde çıkardı.
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException(
        "Jwt:Key en az 32 karakter (256 bit) olmalı — kısa bir imzalama anahtarı " +
        "token'ların taklit edilmesini kolaylaştırır.");

// Issuer/Audience sır değil ama eksik olmaları aynı sınıftan bir kusur taşıyordu:
// TokenService bunları null yazabiliyor, doğrulama ise ValidateIssuer/ValidateAudience
// ile eşleşme arıyor. Sonuç, açılan ama HER girişte 401 üreten bir kurulum olurdu —
// denetimin bütün amacı bu hatayı ilk isteğe değil açılışa çekmek.
RequiredSetting(builder.Configuration, "Jwt:Issuer",
    "Token'ın kim tarafından üretildiğini bildirir ve doğrulamada eşleşmesi aranır.");
RequiredSetting(builder.Configuration, "Jwt:Audience",
    "Token'ın kimin için üretildiğini bildirir ve doğrulamada eşleşmesi aranır.");

// Token ömürleri de açılışta denetleniyor. Sır değiller ama aynı kusuru taşıyorlardı:
// TokenService ve AuthService bunları int.Parse(...!) ile okuyor, yani eksik ya da
// sayı olmayan bir değer ilk giriş denemesinde patlardı.
foreach (var key in new[] { "Jwt:AccessTokenMinutes", "Jwt:RefreshTokenDays" })
    if (!int.TryParse(builder.Configuration[key], out var value) || value <= 0)
        throw new InvalidOperationException(
            $"'{key}' pozitif bir tam sayı olmalı (bulunan: '{builder.Configuration[key]}').");

// Add services to the container.

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IPlatformAuthService, PlatformAuthService>();

// Giriş denemesi sınırı. Sayaç veritabanında duruyor (yeniden başlatma sıfırlamasın) ve
// iki giriş kapısına da aynı servis hizmet ediyor — bkz. LoginThrottle.
builder.Services.Configure<LoginThrottleOptions>(
    builder.Configuration.GetSection("Security:Login"));
builder.Services.AddScoped<ILoginThrottle, LoginThrottle>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAccountTokenService, AccountTokenService>();
builder.Services.AddScoped<ITenantProvisioner, TenantProvisioner>();
builder.Services.AddScoped<IDatasetImportService, DatasetImportService>();
builder.Services.AddScoped<IDatasetQueryExecutor, DatasetQueryExecutor>();
builder.Services.AddScoped<IRelationDetector, RelationDetector>();
builder.Services.AddScoped<IDatasetRowWriter, DatasetRowWriter>();
builder.Services.AddScoped<IDatasetIndexService, DatasetIndexService>();
builder.Services.AddScoped<ITenantCatalogLoader, TenantCatalogLoader>();

// İzleyiciler: kaydedilmiş bir planın ölçülmesi (evaluator), koşunun yürütülüp durumun
// güncellenmesi (runner) ve eşik uyarısının firmaya bildirilmesi (notifier).
builder.Services.AddScoped<IWatchEvaluator, WatchEvaluator>();
builder.Services.AddScoped<IWatchRunner, WatchRunner>();
builder.Services.AddScoped<IWatchScheduler, WatchScheduler>();

// Koşu geçmişinin bakımı: izleyici başına saklanan koşu sayısına tavan koyar.
builder.Services.AddScoped<IWatchRunCleaner, WatchRunCleaner>();

// Uyarının kanalları. İkisi de somut tipiyle kaydediliyor, IWatchNotifier ise ikisini
// saran birleştirici: koşuyu yürüten kod tek bir bildirici görüyor, kanal listesi
// burada duruyor. Üçüncü bir kanal (ör. SMS) eklenecekse değişen tek yer bu satır.
builder.Services.AddScoped<SignalRWatchNotifier>();
builder.Services.AddScoped<WatchEmailNotifier>();
builder.Services.AddScoped<IWatchNotifier>(sp => new CompositeWatchNotifier(
    new IWatchNotifier[]
    {
        sp.GetRequiredService<SignalRWatchNotifier>(),
        sp.GetRequiredService<WatchEmailNotifier>()
    },
    sp.GetRequiredService<ILogger<CompositeWatchNotifier>>()));

// E-posta. Ayar yoksa (Email:Host boş) gönderim KAPALI ve sistem çalışmaya devam eder —
// uyarı zaten veritabanında ve rozette duruyor. Teslimde dış bir SMTP servisi yok:
// docker-compose'daki Mailpit gönderilen postayı yakalayıp tarayıcıda gösteriyor, yani
// teslim alan kişinin hiçbir yere hesap açması ya da gerçek bir parola girmesi gerekmiyor.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddHttpContextAccessor();

// Tek örnek, iki arayüz: okuma sözleşmesi her yere enjekte ediliyor, yazma yeteneği
// (arka plan işinin tenant'ı elle kurması) yalnız ona ihtiyacı olana veriliyor.
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<TenantContext>());

// Örnek soru üretimi SINGLETON: sonuçlar önbellekte tutuluyor ve üretim arka planda
// çalışıyor (istek ömründen uzun), o yüzden scoped olamaz.
builder.Services.AddSingleton<IAskSuggestionService, AskSuggestionService>();

// Doğal dil → sorgu planı. Model kendi makinemizde (Ollama) çalışır: veri kurum dışına
// çıkmaz, KVKK argümanı buna dayanıyor.
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.AddHttpClient<IQueryPlannerService, QueryPlannerService>((sp, client) =>
{
    var ollama = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
    client.BaseAddress = new Uri(ollama.BaseUrl);
    // Zaman aşımı HttpClient üzerinde: ilk çağrıda model belleğe yükleniyor ve uzun sürüyor.
    client.Timeout = TimeSpan.FromSeconds(ollama.TimeoutSeconds);
});

// Belgeden veri çıkarımı. Ayrı bir istemci çünkü görsel model metin modelinden çok daha
// yavaş (belge başına ~30 sn) ve kendi bağlam ayarını taşıyor (bkz. VisionOptions).
builder.Services.Configure<VisionOptions>(builder.Configuration.GetSection("Vision"));
builder.Services.AddHttpClient<IOllamaVisionClient, OllamaVisionClient>((sp, client) =>
{
    var ollama = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
    var vision = sp.GetRequiredService<IOptions<VisionOptions>>().Value;
    client.BaseAddress = new Uri(ollama.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(vision.TimeoutSeconds);
});
builder.Services.AddScoped<IDocumentVisionService, DocumentVisionService>();

// ---------------------------------------------------------------------------
// Asenkron belge işleme.
//
// Neden gerekli: bir belgenin okunması 30-150 saniye sürüyor (görsel model, tek GPU) ve
// çok sayfalı belgede bu süre katlanıyor. Bu kadar uzun bir işi HTTP isteğinin içinde
// tutmak üç yerde kırılır — vekil sunucu bağlantıyı keser, kullanıcı sekmeyi kapatınca
// iş kaybolur, ekran o süre boyunca kilitli kalır. Kuyruk bunu tersine çevirir: istek
// işi kaydeder ve hemen döner, sonuç hazır olunca kullanıcıya bildirilir.
// ---------------------------------------------------------------------------
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    // Hangfire kendi tablolarını AYRI bir şemada kurar: uygulama şeması onun tablolarıyla
    // karışmasın, EF göçleri de onları görüp yönetmeye kalkmasın.
    .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(
        builder.Configuration.GetConnectionString("DefaultConnection"))));

builder.Services.AddScoped<IDocumentJobRunner, DocumentJobRunner>();
builder.Services.AddScoped<IJobNotifier, SignalRJobNotifier>();
builder.Services.AddScoped<IDocumentJobCleaner, DocumentJobCleaner>();

// İşçi (worker) sayısı BİR — ve bu ayarın gerekçesi donanımda:
//
// Görsel model tek ekran kartında çalışıyor ve 8 GB'ın tamamına yakınını kullanıyor
// (bkz. VisionOptions.NumCtx = 4096, ölçülmüş değer). İki belgeyi aynı anda işlemek
// kuyruğu hızlandırmaz, çünkü darboğaz kuyruk değil GPU; ikisi birden sığmadığında model
// katmanları belleğe girip çıkmaya başlar ve TOPLAM süre uzar. Sıraya dizmek, aynı işi
// öngörülebilir sürede bitirir.
//
// Testlerde sunucu açılmaz (Hangfire:RunServer = false): iş kuyruğa girer ama arka planda
// kendiliğinden çalışmaz, testler çalıştırıcıyı elle tetikleyip sonucu deterministik
// olarak sınar. Aksi hâlde testler zamanlamaya bağımlı hâle gelirdi.
// İKİ AYRI SUNUCU, İKİ AYRI KUYRUK.
//
// Önceden tek sunucu vardı, tek işçiyle ve `Queues = { "documents", "default" }` ile —
// ama hiçbir iş "documents" kuyruğuna ATANMIYORDU (bkz. IDocumentJobRunner.RunAsync,
// artık [Queue] taşıyor). Yani belge işleri, bakım işleri ve izleyici taraması hepsi tek
// bir "default" kuyruğunda sıraya giriyordu. Bedeli ölçülebilir bir güvence ihlaliydi:
// 20 fatura yüklendiğinde işçi ~40 dakika belgelerle meşgul oluyor, bu sürede biriken
// izleyici taramaları hiç koşmuyor ve "bir izleyici en fazla beş dakika geç çalışır"
// sözü tutmuyordu.
//
// Belge sunucusu TEK işçide kalıyor ve bu bilinçli: darboğaz kuyruk değil, 8 GB'a sığan
// tek model. İkinci belgeyi paralel işlemek toplam süreyi uzatır. Bakım sunucusu ayrı bir
// işçi, çünkü yaptığı iş GPU değil birkaç SQL sorgusu.
if (builder.Configuration.GetValue("Hangfire:RunServer", true))
{
    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 1);
        options.Queues = new[] { IDocumentJobRunner.DocumentQueue };
        options.ServerName = $"{Environment.MachineName}:belge";
    });

    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = 1;
        options.Queues = new[] { "default" };
        options.ServerName = $"{Environment.MachineName}:bakim";
    });
}

// Canlı bildirim: iş bitince kullanıcıya haber verilir. Yoklama (polling) yerine seçildi,
// çünkü izleyiciler adımı da aynı altyapıya binecek — kurulum bir kez ödeniyor.
//
// Alan adları AÇIKÇA camelCase'e sabitleniyor: REST uçları bu biçimde konuşuyor ve
// istemci tek bir ayrıştırma kuralı bilmeli. Varsayılana bırakılsaydı aynı istemci
// kodu iki farklı yazımla uğraşırdı.
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var jwt = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwt["Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };

        // SignalR bağlantısı için token'ı adres satırından da kabul et.
        //
        // Sebebi tarayıcı kısıtı: WebSocket el sıkışmasını başlatan JavaScript API'si özel
        // başlık (Authorization) göndermeye izin vermez. SignalR bu yüzden token'ı sorgu
        // dizesinde taşır. Kabul YALNIZ /hubs altında geçerli — normal API uçlarında
        // token'ın adreste taşınması onu sunucu günlüklerine ve tarayıcı geçmişine
        // düşürürdü.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    context.Token = token;

                return Task.CompletedTask;
            }
        };
    });

// İki kimlik dünyasını birbirinden ayıran politikalar (bkz. AuthPolicies).
// Düz [Authorize] "geçerli imzalı herhangi bir token" demek olduğundan yetmez:
// platform token'ı da geçerli imzalıdır. Politikalar claim varlığını şart koşar.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.TenantUser, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.TenantIdClaim))
    .AddPolicy(AuthPolicies.PlatformAdmin, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.PlatformAdminClaim, "true"));

// Merkezi hata yönetimi: tüm hataları tek tip ProblemDetails'e oturtur.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// ---------------------------------------------------------------------------
// CORS.
//
// İki Flutter web istemcisi (müşteri paneli ve platform paneli) API'den AYRI birer
// origin'de çalışıyor; tarayıcı, sunucu izin vermedikçe bu isteklere cevabı vermez.
//
// Eskiden AllowAnyOrigin'di ve gerekçesi "kimlik cookie değil Bearer başlığıyla
// taşınıyor, o yüzden güvenli" idi. Doğru ama YETERSİZ bir gerekçe: cookie olmadığı için
// tarayıcının oturumu otomatik eklemesi (CSRF) gerçekten mümkün değil, ama açık origin
// listesi başka bir şeyi mümkün kılıyor — herhangi bir web sayfası, ziyaretçisinin
// tarayıcısından bu API'ye istek atıp CEVABI OKUYABİLİR. Token'ı olmayan bir sayfa için
// bu yalnız 401 demek; token'ı bir şekilde ele geçirmiş (ör. XSS ile aynı makinede
// çalışan) bir sayfa için ise verinin dışarı taşınacağı hazır bir kapı.
//
// Liste AYARDAN okunuyor, koda gömülmüyor: teslim alan kişi paneli başka bir portta ya
// da makinede çalıştırdığında derlemeye değil ayar dosyasına (ya da
// Cors__AllowedOrigins__0 ortam değişkenine) dokunacak.
//
// AllowCredentials BİLİNÇLİ OLARAK YOK: kimlik Bearer başlığında taşınıyor, tarayıcının
// kendiliğinden göndereceği bir cookie/kimlik yok. Açılsaydı CSRF yüzeyi bedavaya açılırdı.
// ---------------------------------------------------------------------------
const string CorsPolicy = "client";

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
    options.AddPolicy(CorsPolicy, policy =>
    {
        if (allowedOrigins.Length == 0)
        {
            // Liste boşsa hiçbir çapraz-origin isteğe izin verilmez. Sessizce "hepsine
            // izin ver"e düşmek, ayarı unutan kurulumu en gevşek hâle sokmak olurdu;
            // güvenlik ayarlarının varsayılanı en dar seçenek olmalı.
            return;
        }

        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
    }));

// ---------------------------------------------------------------------------
// KAYIT UCUNDA HIZ SINIRI
//
// `POST /api/auth/register` kimlik doğrulamasız, ve giriş sayacından bağımsızdı. İki
// ayrı kusuru birden taşıyordu:
//
// 1. HESAP SAYIMI: var olan bir e-posta için 409 + "Bu e-posta zaten kayıtlı." dönüyor,
//    sorgu da IgnoreQueryFilters ile bütün firmalarda arıyor. Yani uç, "bu adres
//    platformda kayıtlı mı" sorusuna sınırsız hızda cevap veren bir araçtı. Giriş
//    tarafında hesap sayımı özenle kapatılmışken (sayaç kayıtsız e-postaları da tutuyor)
//    aynı bilgi buradan bedavaya alınabiliyordu — üstelik elde edilen geçerli adres
//    listesi, bilinçli olarak kabul edilmiş olan password spraying'in tam da girdisi.
// 2. KAYNAK TÜKETME: her çağrı bir Tenant, bir User ve GERÇEK bir PostgreSQL şeması
//    açıyor. Temizleyen bir bakım işi yok; açılıştaki şema eşitlemesi hepsini taradığı
//    için uygulama giderek yavaş açılır. Ayrıca her çağrı ~100 ms'lik BCrypt maliyeti.
//
// Sınır IP başına: burada IP'ye bağlamak doğru, çünkü giriş sayacının aksine korunacak
// bir "hesap" yok — henüz var olmayan bir hesap açılıyor. Kapatılabilir olması şart:
// testler tek IP'den yüzlerce kayıt yapıyor.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(AuthPolicies.RegisterRateLimit, http =>
    {
        // Ayar İSTEK anında okunuyor, `builder.Configuration` üzerinden açılışta değil.
        // Sebebi somut: Program.cs gövdesi `builder.Build()`'den ÖNCE koşuyor, test
        // altyapısının ayar geçersiz kılmaları ise ondan SONRA uygulanıyor. Açılışta
        // okunduğunda testlerin "sınırı kapat" ayarı görülmüyor ve testler birbirinin
        // kotasını yiyip 429 alıyordu.
        var config = http.RequestServices.GetRequiredService<IConfiguration>();

        if (!config.GetValue("Security:Register:Enabled", true))
            return RateLimitPartition.GetNoLimiter("disabled");

        // Vekil sunucu arkasında bütün istekler tek IP'den görünebilir; o kurulumda
        // sınır bir bütün olarak uygulanır. Bilinen kısıt, gizlenmiyor.
        var key = http.Connection.RemoteIpAddress?.ToString() ?? "bilinmeyen";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("Security:Register:MaxPerWindow", 5),
            Window = TimeSpan.FromMinutes(
                config.GetValue("Security:Register:WindowMinutes", 15)),
            QueueLimit = 0
        });
    });
});

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Pipeline'ın en başı: sonraki her katmandan gelen yakalanmamış istisnaları
// GlobalExceptionHandler'a yönlendirir.
app.UseExceptionHandler();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Hız sınırı kimlik doğrulamasından ÖNCE: korunan uç zaten kimlik doğrulamasız, ve
// sınırın amacı isteği mümkün olan en erken noktada elemek.
app.UseRateLimiter();

app.UseCors(CorsPolicy);

if (allowedOrigins.Length == 0)
    app.Logger.LogWarning(
        "Cors:AllowedOrigins boş — tarayıcıdan gelen çapraz-origin istekler reddedilecek. " +
        "Web paneli kullanılacaksa panelin adresi bu listeye eklenmeli.");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Canlı bildirim kanalı. Token'ı sorgu dizesinden okuma izni yalnız bu yol için verildi
// (bkz. JwtBearerEvents yukarıda).
//
// CloseOnAuthenticationExpiration: kimlik yalnız EL SIKIŞMADA doğrulanıyordu, yani bir
// kez kurulmuş WebSocket bağlantısı token ömrü dolduktan sonra da açık kalıyor ve grup
// üyeliği bağlantı yaşadığı sürece sürüyordu. Sonucu şuydu: kullanıcının rolü düşürülse,
// hesabı silinse ya da firma askıya alınsa bile REST istekleri reddedilirken açık
// sekmedeki hub bağlantısı kapanmıyor — firma geneline giden izleyici uyarıları ve kendi
// işlerinin başlıkları (dosya adı, hata metni) sokete akmaya devam ediyordu. Bu ayarla
// bağlantı, token'ın süresi dolduğu anda kapanıyor; istemci zaten taze token'la yeniden
// bağlanıyor (accessTokenFactory).
app.MapHub<JobsHub>("/hubs/jobs", options => options.CloseOnAuthenticationExpiration = true);

// Açılışta migration + eksik tenant şemalarını tamamlama (Spring ApplicationRunner
// karşılığı). AppDbContext scoped olduğundan istek dışında elle scope açmak gerekir.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync(); // taze DB'de (test dahil) tabloları kurar

    var provisioner = scope.ServiceProvider.GetRequiredService<ITenantProvisioner>();
    var count = await provisioner.SyncAllSchemasAsync();
    app.Logger.LogInformation("Tenant şema senkronizasyonu: {Count} tenant kontrol edildi.", count);

    // Platform yöneticisini ayarlardan tohumla. Bilinçli olarak PUBLIC bir kayıt ucu
    // yok: platform kimliğini yalnızca sunucu ayarına (appsettings.Development.json ya
    // da PlatformAdmin__Email / PlatformAdmin__Password ortam değişkenleri) erişebilen
    // kişi belirler.
    var platformAuth = scope.ServiceProvider.GetRequiredService<IPlatformAuthService>();
    await platformAuth.EnsureSeedAdminAsync();
}

// Belge işlerinin bakımı: asılı kalmış işleri kapatır, onaylanmamış görüntüleri ve eski
// kayıtları düşürür (bkz. DocumentJobCleaner). Saatlik yeterli — temizlediği şeylerin
// hiçbiri acil değil, ama biriktiğinde veritabanını şişirirler.
//
// Yalnız Hangfire sunucusunun açık olduğu yerde kaydediliyor: testlerde işçi çalışmadığı
// için bu kaydın bir karşılığı olmazdı.
if (app.Configuration.GetValue("Hangfire:RunServer", true))
{
    var recurring = app.Services.GetRequiredService<IRecurringJobManager>();

    recurring.AddOrUpdate<IDocumentJobCleaner>(
        "document-job-cleanup", c => c.CleanAsync(), Cron.Hourly);

    // İzleyici taraması. Beş dakikada bir yeterli: en sık koşu sıklığı 15 dakika
    // (bkz. WatchIntervals), yani bir izleyici en fazla beş dakika geç çalışır. Daha sık
    // taramak aynı işi daha çok boş sorguyla yapmak olurdu.
    //
    // Tarama, HANGFIRE SUNUCUSUYLA BİRLİKTE kapanıyor: testlerde işçi çalışmadığı için
    // izleyiciler kendiliğinden koşmaz, testler zamanlayıcıyı elle tetikler.
    recurring.AddOrUpdate<IWatchScheduler>(
        "watch-sweep", s => s.SweepAsync(), "*/5 * * * *");

    // Koşu geçmişinin bakımı GÜNLÜK. Belge bakımından (saatlik) daha seyrek, çünkü
    // temizlediği şey birikmesi haftalar süren bir fazlalık: on beş dakikada bir koşan
    // bir izleyici bile tavana ancak beş günde ulaşıyor. Daha sık koşmak, hiçbir şey
    // silmeyen bir sorguyu günde 24 kez tekrarlamak olurdu.
    recurring.AddOrUpdate<IWatchRunCleaner>(
        "watch-run-cleanup", c => c.CleanAsync(), Cron.Daily);

    // Giriş sayaçlarının bakımı da GÜNLÜK. Sayaç satırları normal kullanımda zaten
    // başarılı girişte siliniyor; biriken şey, var olmayan e-postalara yapılmış
    // denemelerden kalan artıklar. Bakım olmasaydı rastgele adres deneyen bir saldırgan
    // tabloyu istediği kadar büyütebilirdi.
    recurring.AddOrUpdate<ILoginThrottle>(
        "login-attempt-cleanup", t => t.CleanAsync(), Cron.Daily);
}

// Geliştirmede: uygulama ayağa kalkınca varsayılan tarayıcıyı Swagger'a aç.
// (dotnet run, launchSettings'teki launchBrowser'ı dinlemez; bu o boşluğu doldurur.)
// ApplicationStarted = "artık istek kabul ediyorum" anı. Testlerdeki TestServer
// gerçek bir porta bağlanmadığından app.Urls boş kalır → tarayıcı açılmaz.
if (app.Environment.IsDevelopment())
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        // https sertifika uyarısını atlamak için http adresini tercih et.
        var url = app.Urls.FirstOrDefault(u => u.StartsWith("http://"))
                  ?? app.Urls.FirstOrDefault();
        if (string.IsNullOrEmpty(url)) return; // adres yok (ör. test) → atla

        try
        {
            // UseShellExecute = true → Windows'ta varsayılan tarayıcıyı çalıştırır.
            Process.Start(new ProcessStartInfo { FileName = $"{url}/swagger", UseShellExecute = true });
        }
        catch { /* tarayıcı açılamazsa uygulamayı düşürme */ }
    });
}

app.Run();

// WebApplicationFactory<Program>'ın test projesinden erişebilmesi için —
// top-level statement'ların ürettiği sınıf aksi halde internal kalır.
public partial class Program { }
