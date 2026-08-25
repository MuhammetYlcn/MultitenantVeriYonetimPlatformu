using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Hubs;
using VeriYonetim.Api.Middleware;
using VeriYonetim.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IPlatformAuthService, PlatformAuthService>();
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
if (builder.Configuration.GetValue("Hangfire:RunServer", true))
    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 1);
        options.Queues = new[] { "documents", "default" };
    });

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!))
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

// CORS: Flutter web istemcisi ayrı bir origin'den (localhost:farklı-port) istek atar;
// tarayıcı aksi halde engeller. Kimlik Bearer header ile taşındığından (cookie değil)
// AllowAnyOrigin güvenli. Yalnızca geliştirme için gevşek tutuldu.
builder.Services.AddCors(options =>
    options.AddPolicy("dev", policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

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

app.UseCors("dev");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Canlı bildirim kanalı. Token'ı sorgu dizesinden okuma izni yalnız bu yol için verildi
// (bkz. JwtBearerEvents yukarıda).
app.MapHub<JobsHub>("/hubs/jobs");

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
