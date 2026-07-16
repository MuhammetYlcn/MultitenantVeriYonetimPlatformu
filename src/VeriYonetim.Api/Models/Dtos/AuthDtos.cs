using System.ComponentModel.DataAnnotations;

namespace VeriYonetim.Api.Models.Dtos;

public record RegisterRequest(
    [Required, MaxLength(200)] string TenantName,
    [Required, MaxLength(100)] string TenantSlug,
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MinLength(8)] string Password);

public record LoginRequest(
    [Required] string TenantSlug,
    [Required, EmailAddress] string Email,
    [Required] string Password);

public record RefreshRequest([Required] string RefreshToken);

public record AuthResponse(Guid UserId, Guid TenantId, string Email, string Role,
    string Token, string RefreshToken);

public record AuthResult(bool Success, string Message, AuthResponse? Data = null);
