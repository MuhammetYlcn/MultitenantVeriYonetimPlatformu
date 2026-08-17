using System.Text.Json;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Models.Dtos;

/// <summary>
/// Belgeden çıkarılan verinin ÖNİZLEMESİ. Hiçbir şey kaydedilmez.
///
/// Neden kaydetmiyoruz: granülerlik kararı (bir belge kaç satır) ve alan eşlemesi
/// kullanıcının onayından geçmeli. Model önerir, sunucu doğrular ve budar, kullanıcı
/// onay ekranında çevirir. Doğrudan yazmak, modelin okuduğunu kullanıcının gördüğü
/// gerçek sanmasına yol açardı.
/// </summary>
/// <param name="Columns">Satırların kolon adları (belge alanları + kalem kolonları).</param>
/// <param name="Rows">Ham satırlar; değerler metin, tip dönüşümü onaydan sonra yapılır.</param>
/// <param name="Errors">Şemaya uymayan hücreler — onay ekranı bunları işaretler.</param>
/// <param name="Warnings">Bağlam taşması, düşürülen kalem satırı, düzleştirilen yapı…</param>
/// <param name="Suspect">true ise çıkarım güvenilir sayılmıyor (bağlam taşması).</param>
public record DocumentExtractionResponse(
    Guid DatasetId,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string[]> Rows,
    IReadOnlyList<RowError> Errors,
    IReadOnlyList<string> Warnings,
    bool Suspect,
    string Model,
    int PromptTokens,
    int NumCtx,
    int LongEdge,
    int Attempts,
    int DurationMs);

/// <summary>
/// KEŞİF geçişinin sonucu: belge şemasız okundu, çıkan taslak var olan setlerle eşleştirildi.
///
/// Buradan hiçbir şey kaydedilmez — `extract` gibi bu da önizlemedir. Fark, hedef setin
/// önceden bilinmemesi: kullanıcı önerilen setlerden birini seçecek ya da yeni set açacak.
/// </summary>
/// <param name="DocumentType">Modelin okuduğu belge türü ("fatura", "fis"…); yeni set adı önerisi buradan.</param>
/// <param name="Columns">Belgeden çıkan taslak şema — adlar modelden, TİPLER değerlerden algılandı.</param>
/// <param name="Rows">Ham satırlar (tip dönüşümü onaydan sonra).</param>
/// <param name="Matches">Eşiği geçen aday setler, puanı yüksekten düşüğe. Boşsa yeni set önerilir.</param>
/// <param name="SuggestedName">Yeni set açılacaksa önerilen ad.</param>
public record DocumentDiscoveryResponse(
    string? DocumentType,
    IReadOnlyList<ColumnSchema> Columns,
    IReadOnlyList<string[]> Rows,
    IReadOnlyList<DatasetMatchDto> Matches,
    string SuggestedName,
    IReadOnlyList<string> Warnings,
    bool Suspect,
    string Model,
    int PromptTokens,
    int NumCtx,
    int LongEdge,
    int Attempts,
    int DurationMs);

/// <summary>
/// Onay ekranından gelen KESİNLEŞMİŞ tablo — kullanıcı düzeltmelerini yaptı, kaydedilecek.
///
/// Neden belgenin kendisi değil de tablo gönderiliyor: kaydedilen şey modelin okuduğu değil,
/// kullanıcının ONAYLADIĞI hâldir. Belgeyi ikinci kez okutmak hem 30-45 saniye daha sürer
/// hem de kullanıcının ekranda düzelttiği hücreleri geri alırdı.
/// </summary>
/// <param name="JobId">Bu tablonun geldiği iş. Verilirse belge görüntüsü onaydan sonra
/// silinir: görüntü işin ömrüne bağlı bir ara üründür, kalıcı olan kaydedilen satırlardır.</param>
public record DocumentConfirmRequest(
    IReadOnlyList<string>? Columns,
    IReadOnlyList<string[]>? Rows,
    Guid? JobId = null);

/// <summary>Kaydetme sonucu. Satırlar EKLENİR (içe aktarma gibi eskileri silmez).</summary>
public record DocumentConfirmResponse(Guid DatasetId, int SavedRows, int TotalRows);

/// <summary>
/// Kuyruğa alınmış bir belge işinin durumu.
///
/// Belge okuma 30-150 saniye sürdüğü için uçlar sonucu değil bu kaydı döndürüyor:
/// istek hemen kapanıyor, kullanıcı ekranda kilitli kalmıyor, iş bitince haber geliyor.
/// </summary>
/// <param name="Kind">"extract" (hedef şema belli) ya da "discover" (şemasız keşif).</param>
/// <param name="Status">queued → running → succeeded | failed.</param>
/// <param name="Result">Yalnız bittiğinde dolu. İçeriği türe göre değişir: çıkarım
/// sonucu ya da keşif sonucu. İstemciye saklandığı biçimde geçiriliyor.</param>
/// <param name="ConfirmedAt">Dolu ise bu belgeden çıkan satırlar zaten kaydedilmiş;
/// ekran ikinci kaydetmeyi kapatır ve sunucu da reddeder.</param>
public record DocumentJobResponse(
    Guid Id,
    string Kind,
    string Status,
    Guid? DatasetId,
    string? FileName,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? ConfirmedAt,
    JsonElement? Result);

/// <summary>
/// İş kaydını istemciye giden biçime çevirir.
///
/// Sonuç JSON'u YENİDEN SERİLEŞTİRİLMİYOR: metin olarak saklanan gövde JsonElement'e
/// ayrıştırılıp olduğu gibi geçiriliyor. Aksi hâlde aynı sözleşme iki kez tarif edilir
/// ve iki tarafın biçimi zamanla ayrışırdı.
/// </summary>
public static class DocumentJobMapper
{
    public static DocumentJobResponse ToResponse(DocumentJob job, bool includeResult = true)
    {
        JsonElement? result = null;

        if (includeResult && !string.IsNullOrWhiteSpace(job.ResultJson))
            result = JsonSerializer.Deserialize<JsonElement>(job.ResultJson);

        return new DocumentJobResponse(
            job.Id,
            job.Kind,
            job.Status,
            job.DatasetId,
            job.FileName,
            job.Error,
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt,
            job.ConfirmedAt,
            result);
    }
}

/// <summary>
/// Aday veri setiyle eşleşme. Puan tek başına yeterli değil: kullanıcının kararı
/// verebilmesi için NEYİN eşleştiği, neyin eksik ve neyin fazla kaldığı da gönderiliyor.
/// </summary>
/// <param name="Score">0-1 arası benzerlik (Dice). Eşiğin altındakiler hiç gönderilmez.</param>
/// <param name="Mappings">belge kolonu → set kolonu; tip uyuşmazlığı işaretli.</param>
/// <param name="MissingColumns">Sette var, belgede çıkmadı — o hücreler boş kalır.</param>
/// <param name="ExtraColumns">Belgede var, sette yok — bu seti seçerse kaybolur.</param>
public record DatasetMatchDto(
    Guid DatasetId,
    string Name,
    double Score,
    IReadOnlyList<ColumnMapping> Mappings,
    IReadOnlyList<string> MissingColumns,
    IReadOnlyList<string> ExtraColumns);
