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
/// Bir aday veri setiyle eşleşme. Puan tek başına yeterli değil: kullanıcının kararı
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
