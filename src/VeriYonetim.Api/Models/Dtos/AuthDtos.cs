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

// Admin yeni kullanıcıyı DAVET eder; şifre GİRMEZ. Şifreyi kullanıcı, davet
// bağlantısını açtığında kendisi belirler — böylece Admin hiçbir aşamada başkasının
// şifresini bilmez. (Eski CreateUserRequest bilinçli olarak kaldırıldı: Admin'in
// şifre girdiği akış, "şifreyi yalnız sahibi bilir" ilkesini bozuyordu.)
public record InviteUserRequest(
    [Required(ErrorMessage = "E-posta gerekli.")]
    [EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi girin.")]
    [MaxLength(320)] string Email,
    [Required(ErrorMessage = "Rol gerekli.")]
    [RegularExpression("^(Viewer|Editor|Admin)$",
        ErrorMessage = "Rol 'Viewer', 'Editor' veya 'Admin' olmalı.")]
    string Role);

// Davet/sıfırlama bağlantısı. E-posta gönderimi (SMTP) kapsam dışı olduğundan ham
// token yanıtla Admin'e döner ve arayüzde kopyalanabilir bir bağlantı olarak gösterilir.
// Ham token YALNIZCA burada, bir kez görünür; veritabanında yalnız özeti saklanır.
public record AccountTokenResponse(string Token, string Email, string? Role,
    DateTime ExpiresAt, string Purpose);

// Davet/sıfırlama bağlantısı açıldığında ekranda ne yazacağını belirlemek için.
public record AccountTokenInfoResponse(string Purpose, string Email, string? Role,
    string TenantName, DateTime ExpiresAt);

// Kullanıcı kendi şifresini belirler (davette hesap oluşur, sıfırlamada değişir).
public record AcceptAccountTokenRequest(
    [Required(ErrorMessage = "Şifre gerekli.")]
    [MinLength(8, ErrorMessage = "Şifre en az 8 karakter olmalı.")]
    string Password);

// Giriş yapmış kullanıcının kendi şifresini değiştirmesi.
public record ChangePasswordRequest(
    [Required(ErrorMessage = "Mevcut şifre gerekli.")]
    string CurrentPassword,
    [Required(ErrorMessage = "Yeni şifre gerekli.")]
    [MinLength(8, ErrorMessage = "Yeni şifre en az 8 karakter olmalı.")]
    string NewPassword);

// Var olan bir kullanıcının rolünü değiştirmek için (yalnız Admin).
public record UpdateUserRoleRequest(
    [Required(ErrorMessage = "Rol gerekli.")]
    [RegularExpression("^(Viewer|Editor|Admin)$",
        ErrorMessage = "Rol 'Viewer', 'Editor' veya 'Admin' olmalı.")]
    string Role);

public record AuthResponse(Guid UserId, Guid TenantId, string Email, string Role,
    string Token, string RefreshToken);

public record AuthResult(bool Success, string Message, AuthResponse? Data = null);
