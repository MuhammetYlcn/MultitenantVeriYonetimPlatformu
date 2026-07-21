using System.ComponentModel.DataAnnotations;

namespace VeriYonetim.Api.Models.Dtos;

// Slug artık istemciden alınmaz; firma adından sunucuda otomatik türetilir (iç detay).
public record RegisterRequest(
    [Required(ErrorMessage = "Firma adı gerekli.")]
    [MaxLength(200, ErrorMessage = "Firma adı en fazla 200 karakter olabilir.")]
    string TenantName,
    [Required(ErrorMessage = "E-posta gerekli.")]
    [EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi girin.")]
    [MaxLength(320, ErrorMessage = "E-posta en fazla 320 karakter olabilir.")]
    string Email,
    [Required(ErrorMessage = "Şifre gerekli.")]
    [MinLength(8, ErrorMessage = "Şifre en az 8 karakter olmalı.")]
    string Password);

// E-posta artık global benzersiz olduğundan giriş için tenant bilgisi gerekmez.
public record LoginRequest(
    [Required(ErrorMessage = "E-posta gerekli.")]
    [EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi girin.")]
    string Email,
    [Required(ErrorMessage = "Şifre gerekli.")]
    string Password);

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
