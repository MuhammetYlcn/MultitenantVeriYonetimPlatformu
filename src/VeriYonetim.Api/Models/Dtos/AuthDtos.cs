using System.ComponentModel.DataAnnotations;

namespace VeriYonetim.Api.Models.Dtos;

public record RegisterRequest(
    [Required, MaxLength(200)] string TenantName,
    // Slug şema adına dönüşür (tenant_<slug>): sadece küçük harf/rakam/tire,
    // harfle başlar — bu whitelist SQL injection'ı format seviyesinde imkansızlaştırır.
    // 56 = 63 (PostgreSQL tanımlayıcı limiti) - 7 ("tenant_" öneki).
    [Required, MaxLength(56), RegularExpression("^[a-z][a-z0-9-]*$",
        ErrorMessage = "Slug küçük harfle başlamalı; sadece küçük harf, rakam ve tire içerebilir.")]
    string TenantSlug,
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MinLength(8)] string Password);

public record LoginRequest(
    [Required] string TenantSlug,
    [Required, EmailAddress] string Email,
    [Required] string Password);

public record RefreshRequest([Required] string RefreshToken);

public record CreateUserRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MinLength(8)] string Password,
    [Required, RegularExpression("^(Admin|User)$",
        ErrorMessage = "Rol 'Admin' veya 'User' olmalı.")]
    string Role);

public record AuthResponse(Guid UserId, Guid TenantId, string Email, string Role,
    string Token, string RefreshToken);

public record AuthResult(bool Success, string Message, AuthResponse? Data = null);
