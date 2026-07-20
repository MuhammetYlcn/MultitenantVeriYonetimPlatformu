using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace VeriYonetim.Api.Middleware;

// Tüm controller'ların dışındaki güvenlik ağı (Spring @ControllerAdvice karşılığı).
// Yakalanmamış her istisna buraya düşer: sunucuda tam detay loglanır, istemciye
// yalnızca genel bir ProblemDetails döner — stack trace / iç mesaj SIZMAZ.
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IProblemDetailsService _problemDetails;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger,
        IProblemDetailsService problemDetails)
    {
        _logger = logger;
        _problemDetails = problemDetails;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Sunucu tarafı: tam istisna teşhis için loglanır.
        _logger.LogError(exception, "İşlenmeyen istisna: {Message}", exception.Message);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        // İstemci tarafı: sadece genel bilgi — detay yok.
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Sunucu hatası",
                Detail = "İstek işlenirken beklenmeyen bir hata oluştu."
            }
        });
    }
}
