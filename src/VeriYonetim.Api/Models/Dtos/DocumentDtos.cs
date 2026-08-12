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
