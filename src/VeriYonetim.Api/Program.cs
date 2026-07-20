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
builder.Services.AddScoped<ITokenService, TokenService>();
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

// Merkezi hata yönetimi: tüm hataları tek tip ProblemDetails'e oturtur.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

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
}

app.Run();

// WebApplicationFactory<Program>'ın test projesinden erişebilmesi için —
// top-level statement'ların ürettiği sınıf aksi halde internal kalır.
public partial class Program { }
