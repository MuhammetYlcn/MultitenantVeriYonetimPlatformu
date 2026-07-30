using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VeriYonetim.Api.Data;
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, TenantContext>();

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
