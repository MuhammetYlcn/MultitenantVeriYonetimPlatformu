import 'package:flutter/material.dart';
import 'package:flutter/services.dart'; // LogicalKeyboardKey (Enter ile gönder)

import '../api_service.dart';
import '../theme/app_theme.dart';
import '../widgets/charts.dart';
import '../widgets/ui.dart';

// Doğal dilde sorgu ekranı — sohbet biçiminde.
//
// Neden sohbet? Sorgulama tek seferlik bir işlem değil: kullanıcı sorar, cevaba bakar,
// soruyu daraltır. Form + sonuç düzeninde her yeni soru öncekini siler ve karşılaştırma
// imkânı kalmaz. Akış hâlinde ise geçmiş sorular ekranda durur.
//
// Her cevap sonucun kendisiyle birlikte "anladığım sorgu" satırını da taşır. Yalnız sonuç
// göstermek yetmez: modelin soruyu YANLIŞ anladığı durumu kullanıcının fark etmesinin tek
// yolu budur. Üretilen SQL bilinçli olarak gösterilmez — iş kullanıcısı SQL okumaz.

sealed class ChatEntry {
  const ChatEntry();
}

class _Question extends ChatEntry {
  final String text;
  const _Question(this.text);
}

class _Answer extends ChatEntry {
  final AskResult result;
  const _Answer(this.result);
}

class _Failure extends ChatEntry {
  final String message;
  const _Failure(this.message);
}

class _Thinking extends ChatEntry {
  const _Thinking();
}

class AskPage extends StatefulWidget {
  const AskPage({super.key});

  @override
  State<AskPage> createState() => _AskPageState();
}

class _AskPageState extends State<AskPage> {
  final _entries = <ChatEntry>[];
  final _input = TextEditingController();
  final _inputFocus = FocusNode();
  final _scroll = ScrollController();

  List<AiModel> _models = [];
  String? _model;
  bool _sending = false;

  // Kısa, kullanıcıya dönük etiket; ayrıntı ipucu balonunda gösterilir.
  String? _modelError;
  String? _modelErrorDetail;

  // Örnek sorular sunucuda üretiliyor ve her biri gösterilmeden önce doğrulanıyor;
  // bu yüzden ilk açılışta hazır olmayabilir. Hazır değilse EKRANDA HİÇBİR ŞEY
  // GÖSTERİLMEZ — "hazırlanıyor" göstergesi kullanıcıyı boş yere bekletirdi.
  List<String> _suggestions = [];
  int _suggestionTries = 0;

  // Açık sohbet. null ise ilk soruda sunucu yeni bir sohbet açar ve kimliğini döner.
  String? _conversationId;

  static const _maxSuggestionTries = 6;
  static const _suggestionRetryDelay = Duration(seconds: 10);

  @override
  void initState() {
    super.initState();
    _loadModels();
    _loadSuggestions();
  }

  Future<void> _loadSuggestions() async {
    if (!mounted || _suggestionTries >= _maxSuggestionTries) return;
    _suggestionTries++;

    try {
      final result = await ApiService.askSuggestions();
      if (!mounted) return;

      if (result.ready) {
        setState(() => _suggestions = result.questions);
        return;
      }

      // Üretim sürüyor: bir süre sonra tekrar sor.
      await Future.delayed(_suggestionRetryDelay);
      await _loadSuggestions();
    } catch (_) {
      // Öneriler kozmetik; alınamazsa ekran onlarsız çalışır.
    }
  }

  @override
  void dispose() {
    _input.dispose();
    _inputFocus.dispose();
    _scroll.dispose();
    super.dispose();
  }

  Future<void> _loadModels() async {
    try {
      final models = await ApiService.aiModels();
      if (!mounted) return;
      setState(() {
        _models = models;
        _model = models.firstWhere((m) => m.isDefault, orElse: () => models.first).name;
        _modelError = null;
      });
    } catch (e) {
      if (!mounted) return;

      // Kullanıcıya NEDEN çalışmadığını söyle. İki durum bambaşka:
      //   ApiException  → sunucu cevap verdi ve sorunu bildirdi (ör. Ollama kapalı)
      //   diğer istisna → sunucuya hiç ulaşılamadı (uygulama kapalı, ağ kesik)
      // "Model listesi yok" demek ikisini de gizler ve kullanıcı ne yapacağını bilemez.
      setState(() {
        _modelError = e is ApiException
            ? 'Yapay zekâ servisi kapalı'
            : 'Sunucuya ulaşılamıyor';
        _modelErrorDetail = e.toString();
      });
    }
  }

  // Hata rozetine tıklanınca yeniden dene: sunucu geri gelince sayfayı yenilemek
  // gerekmesin.
  Future<void> _retryModels() async {
    setState(() {
      _modelError = null;
      _modelErrorDetail = null;
    });
    await _loadModels();
  }

  Future<void> _send() async {
    final question = _input.text.trim();
    if (question.isEmpty || _sending) return;

    setState(() {
      _entries.add(_Question(question));
      _entries.add(const _Thinking());
      _sending = true;
      _input.clear();
    });
    _scrollToEnd();

    ChatEntry entry;
    String? conversationId;
    try {
      final result = await ApiService.ask(question,
          model: _model, conversationId: _conversationId);
      entry = _Answer(result);
      conversationId = result.conversationId;
    } catch (e) {
      entry = _Failure(e.toString());
    }

    if (!mounted) return;
    setState(() {
      _entries.removeLast(); // "düşünüyor" satırını kaldır
      _entries.add(entry);
      _sending = false;
      // Sunucu yeni sohbet açtıysa kimliğini sakla: sonraki sorular buraya eklenecek.
      if (conversationId != null) _conversationId = conversationId;
    });
    _scrollToEnd();
    _inputFocus.requestFocus();
  }

  // Yeni içerik eklendikten SONRA kaydır: yükseklik ancak çizimden sonra bilinir.
  void _scrollToEnd() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!_scroll.hasClients) return;
      _scroll.animateTo(
        _scroll.position.maxScrollExtent,
        duration: const Duration(milliseconds: 260),
        curve: Curves.easeOutCubic,
      );
    });
  }

  void _ask(String text) {
    _input.text = text;
    _send();
  }

  // Yeni sohbet: ekranı ve sohbet bağını temizler. Eski sohbet SİLİNMEZ, sunucuda durur.
  void _newChat() => setState(() {
        _entries.clear();
        _conversationId = null;
      });

  Future<void> _openHistory() async {
    final selected = await showDialog<String>(
      context: context,
      builder: (_) => const _HistoryDialog(),
    );
    if (selected == null || !mounted) return;
    await _loadConversation(selected);
  }

  // Geçmiş bir sohbeti açar. Yanıtlar KAYDEDİLDİĞİ HÂLİYLE gösterilir; yeniden
  // sorulmaz — veri o günden beri değişmiş olabilir ve geçmiş bir cevabın sonradan
  // değişmesi kafa karıştırırdı.
  Future<void> _loadConversation(String id) async {
    try {
      final detail = await ApiService.conversation(id);
      if (!mounted) return;

      setState(() {
        _conversationId = detail.id;
        _entries
          ..clear()
          ..addAll([
            for (final turn in detail.turns) ...[
              _Question(turn.question),
              _Answer(turn.result),
            ],
          ]);
      });
      _scrollToEnd();
    } catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(e.toString())));
    }
  }

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _Header(
          models: _models,
          selected: _model,
          error: _modelError,
          errorDetail: _modelErrorDetail,
          onSelect: (m) => setState(() => _model = m),
          onRetryModels: _retryModels,
          onNewChat: _entries.isEmpty ? null : _newChat,
          onHistory: _openHistory,
        ),
        const SizedBox(height: 16),
        Expanded(
          child: _entries.isEmpty
              ? _Welcome(onPick: _ask, suggestions: _suggestions)
              : ListView.separated(
                  controller: _scroll,
                  padding: const EdgeInsets.only(bottom: 8),
                  itemCount: _entries.length,
                  separatorBuilder: (_, _) => const SizedBox(height: 14),
                  itemBuilder: (_, i) => switch (_entries[i]) {
                    _Question(:final text) => _QuestionBubble(text: text),
                    _Thinking() => const _ThinkingBubble(),
                    _Failure(:final message) => _FailureCard(message: message),
                    _Answer(:final result) => _AnswerCard(result: result),
                  },
                ),
        ),
        const SizedBox(height: 12),
        _Composer(
          controller: _input,
          focusNode: _inputFocus,
          sending: _sending,
          onSend: _send,
        ),
      ],
    );
  }
}

// --- başlık: model seçici -----------------------------------------------------------

class _Header extends StatelessWidget {
  final List<AiModel> models;
  final String? selected;
  final String? error;
  final String? errorDetail;
  final ValueChanged<String> onSelect;
  final VoidCallback onRetryModels;
  final VoidCallback? onNewChat;
  final VoidCallback onHistory;

  const _Header({
    required this.models,
    required this.selected,
    required this.error,
    required this.errorDetail,
    required this.onSelect,
    required this.onRetryModels,
    required this.onNewChat,
    required this.onHistory,
  });

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;

    return Row(
      children: [
        const IconBadge(icon: Icons.auto_awesome, color: AppColors.brand),
        const SizedBox(width: 12),
        Expanded(child: Text('Modele soru sor', style: t.titleLarge)),
        IconButton(
          onPressed: onHistory,
          icon: const Icon(Icons.history, size: 20),
          tooltip: 'Geçmiş sohbetler',
        ),
        IconButton(
          onPressed: onNewChat,
          icon: const Icon(Icons.add_comment_outlined, size: 19),
          tooltip: 'Yeni sohbet',
        ),
        const SizedBox(width: 8),
        _ModelPicker(
          models: models,
          selected: selected,
          error: error,
          errorDetail: errorDetail,
          onSelect: onSelect,
          onRetry: onRetryModels,
        ),
      ],
    );
  }
}

class _ModelPicker extends StatelessWidget {
  final List<AiModel> models;
  final String? selected;
  final String? error;
  final String? errorDetail;
  final ValueChanged<String> onSelect;
  final VoidCallback onRetry;

  const _ModelPicker({
    required this.models,
    required this.selected,
    required this.error,
    required this.errorDetail,
    required this.onSelect,
    required this.onRetry,
  });

  @override
  Widget build(BuildContext context) {
    if (error != null) {
      // Etiket kullanıcıya dönük ve eyleme çevrilebilir; teknik ayrıntı ipucunda.
      // Tıklayınca yeniden dener — sunucu geri gelince sayfa yenilemek gerekmesin.
      return Tooltip(
        message: '${errorDetail ?? error!}\n\nYeniden denemek için tıkla',
        child: Material(
          color: AppColors.danger.withValues(alpha: 0.12),
          borderRadius: BorderRadius.circular(AppRadius.control),
          child: InkWell(
            onTap: onRetry,
            borderRadius: BorderRadius.circular(AppRadius.control),
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 9),
              decoration: BoxDecoration(
                border: Border.all(color: AppColors.danger.withValues(alpha: 0.4)),
                borderRadius: BorderRadius.circular(AppRadius.control),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const Icon(Icons.cloud_off_outlined, size: 16, color: AppColors.danger),
                  const SizedBox(width: 8),
                  Text(error!,
                      style: const TextStyle(fontSize: 12.5, color: AppColors.danger)),
                  const SizedBox(width: 8),
                  const Icon(Icons.refresh, size: 15, color: AppColors.danger),
                ],
              ),
            ),
          ),
        ),
      );
    }

    if (models.isEmpty) {
      return const SizedBox(
        width: 18,
        height: 18,
        child: CircularProgressIndicator(strokeWidth: 2),
      );
    }

    final current = models.firstWhere((m) => m.name == selected, orElse: () => models.first);

    return PopupMenuButton<String>(
      onSelected: onSelect,
      tooltip: 'Model seç',
      position: PopupMenuPosition.under,
      itemBuilder: (_) => [
        for (final m in models)
          PopupMenuItem(
            value: m.name,
            child: Row(
              children: [
                Icon(
                  m.name == current.name ? Icons.check_circle : Icons.circle_outlined,
                  size: 16,
                  color: m.name == current.name ? AppColors.accent : AppColors.muted,
                ),
                const SizedBox(width: 10),
                Flexible(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(m.name,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: const TextStyle(fontSize: 13.5)),
                      Text(
                        [
                          if (m.parameterSize != null) m.parameterSize!,
                          if (m.quantization != null) m.quantization!,
                          if (m.sizeLabel.isNotEmpty) m.sizeLabel,
                        ].join(' · '),
                        style: const TextStyle(fontSize: 11, color: AppColors.muted),
                      ),
                    ],
                  ),
                ),
                if (m.isDefault) ...[
                  const SizedBox(width: 10),
                  const _Tag(text: 'varsayılan'),
                ],
              ],
            ),
          ),
      ],
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 9),
        decoration: BoxDecoration(
          color: AppColors.surfaceAlt,
          border: Border.all(color: AppColors.border),
          borderRadius: BorderRadius.circular(AppRadius.control),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.memory, size: 16, color: AppColors.accent),
            const SizedBox(width: 8),
            // Kısa ad + Flexible + ellipsis: düğme dar bir alana düşerse taşma yerine
            // kırpılsın. Tam ad ipucunda ve menüde duruyor, bilgi kaybolmuyor.
            Flexible(
              child: Tooltip(
                message: current.name,
                child: Text(current.shortName,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                        fontSize: 12.5, fontWeight: FontWeight.w600)),
              ),
            ),
            const SizedBox(width: 4),
            const Icon(Icons.expand_more, size: 16, color: AppColors.muted),
          ],
        ),
      ),
    );
  }
}

// --- karşılama ----------------------------------------------------------------------

class _Welcome extends StatelessWidget {
  final ValueChanged<String> onPick;

  /// Sunucudan gelen, firmanın KENDİ verisine göre üretilmiş ve doğrulanmış sorular.
  /// Boşsa hiç gösterilmez — sabit örnekler koymak, o kolonlara sahip olmayan bir
  /// firmada tıklandığında hata veren bir vitrin demek olurdu.
  final List<String> suggestions;

  const _Welcome({required this.onPick, required this.suggestions});

  // Metne bakarak uygun simgeyi seçer. Simgeyi sunucudan taşımak yerine burada
  // türetiyoruz: tamamen görsel bir karar, veriyle ilgisi yok.
  static IconData _iconFor(String question) {
    final q = question.toLowerCase();
    if (q.contains('kaç') || q.contains('sayı')) return Icons.tag;
    if (q.contains('geçen') || q.contains('karşılaştır')) return Icons.compare_arrows;
    if (q.contains('ay') || q.contains('yıl') || q.contains('tarih')) return Icons.show_chart;
    if (q.contains('listele') || q.contains('hangi')) return Icons.format_list_numbered;
    if (q.contains('göre') || q.contains('bazında')) return Icons.bar_chart_outlined;
    return Icons.auto_awesome_outlined;
  }

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;

    return Center(
      child: SingleChildScrollView(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 560),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                width: 62,
                height: 62,
                decoration: BoxDecoration(
                  gradient: LinearGradient(
                    colors: [
                      AppColors.brand.withValues(alpha: 0.28),
                      AppColors.accent.withValues(alpha: 0.20),
                    ],
                  ),
                  borderRadius: BorderRadius.circular(20),
                ),
                child: const Icon(Icons.auto_awesome, color: AppColors.brand, size: 28),
              ),
              const SizedBox(height: 18),
              Text('Ne öğrenmek istersin?',
                  style: t.headlineSmall, textAlign: TextAlign.center),
              const SizedBox(height: 8),
              Text(
                'Sorunu gündelik dille yaz. Sistem hangi veri setlerine bakacağına kendisi '
                'karar verir, gerekiyorsa birden fazlasını birleştirir.',
                style: t.bodySmall,
                textAlign: TextAlign.center,
              ),
              if (suggestions.isNotEmpty) ...[
                const SizedBox(height: 26),
                Wrap(
                  alignment: WrapAlignment.center,
                  spacing: 10,
                  runSpacing: 10,
                  children: [
                    for (final question in suggestions)
                      _SampleChip(
                        text: question,
                        icon: _iconFor(question),
                        onTap: () => onPick(question),
                      ),
                  ],
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class _SampleChip extends StatelessWidget {
  final String text;
  final IconData icon;
  final VoidCallback onTap;

  const _SampleChip({required this.text, required this.icon, required this.onTap});

  @override
  Widget build(BuildContext context) {
    return Material(
      color: AppColors.surface,
      borderRadius: BorderRadius.circular(AppRadius.control),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(AppRadius.control),
        hoverColor: AppColors.brand.withValues(alpha: 0.08),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
          decoration: BoxDecoration(
            border: Border.all(color: AppColors.border),
            borderRadius: BorderRadius.circular(AppRadius.control),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon, size: 15, color: AppColors.muted),
              const SizedBox(width: 9),
              Text(text, style: const TextStyle(fontSize: 13)),
            ],
          ),
        ),
      ),
    );
  }
}

// --- sohbet balonları ---------------------------------------------------------------

class _QuestionBubble extends StatelessWidget {
  final String text;
  const _QuestionBubble({required this.text});

  @override
  Widget build(BuildContext context) {
    return Align(
      alignment: Alignment.centerRight,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 520),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
          decoration: BoxDecoration(
            color: AppColors.brand.withValues(alpha: 0.16),
            border: Border.all(color: AppColors.brand.withValues(alpha: 0.35)),
            borderRadius: const BorderRadius.only(
              topLeft: Radius.circular(AppRadius.card),
              topRight: Radius.circular(AppRadius.card),
              bottomLeft: Radius.circular(AppRadius.card),
              bottomRight: Radius.circular(4),
            ),
          ),
          child: Text(text, style: const TextStyle(fontSize: 14, height: 1.4)),
        ),
      ),
    );
  }
}

class _ThinkingBubble extends StatefulWidget {
  const _ThinkingBubble();

  @override
  State<_ThinkingBubble> createState() => _ThinkingBubbleState();
}

class _ThinkingBubbleState extends State<_ThinkingBubble>
    with SingleTickerProviderStateMixin {
  late final AnimationController _c = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 1100),
  )..repeat();

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Align(
      alignment: Alignment.centerLeft,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
        decoration: BoxDecoration(
          color: AppColors.surface,
          border: Border.all(color: AppColors.border),
          borderRadius: BorderRadius.circular(AppRadius.card),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            for (var i = 0; i < 3; i++) ...[
              AnimatedBuilder(
                animation: _c,
                builder: (_, _) {
                  // Noktalar sırayla parlar: bekleme süresi uzun olduğu için (model
                  // ilk çağrıda belleğe yükleniyor) donmuş görünmemesi önemli.
                  final phase = (_c.value - i * 0.18) % 1.0;
                  final t = (1 - (phase * 2 - 1).abs()).clamp(0.0, 1.0);
                  return Container(
                    width: 7,
                    height: 7,
                    margin: const EdgeInsets.symmetric(horizontal: 2.5),
                    decoration: BoxDecoration(
                      color: Color.lerp(AppColors.border, AppColors.brand, t),
                      shape: BoxShape.circle,
                    ),
                  );
                },
              ),
            ],
            const SizedBox(width: 12),
            const Text('düşünüyor…',
                style: TextStyle(fontSize: 13, color: AppColors.muted)),
          ],
        ),
      ),
    );
  }
}

class _FailureCard extends StatelessWidget {
  final String message;
  const _FailureCard({required this.message});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppColors.danger.withValues(alpha: 0.08),
        border: Border.all(color: AppColors.danger.withValues(alpha: 0.35)),
        borderRadius: BorderRadius.circular(AppRadius.card),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(Icons.error_outline, size: 19, color: AppColors.danger),
          const SizedBox(width: 12),
          Expanded(
            child: Text(message,
                style: const TextStyle(fontSize: 13.5, height: 1.45)),
          ),
        ],
      ),
    );
  }
}

// --- cevap kartı --------------------------------------------------------------------

class _AnswerCard extends StatelessWidget {
  final AskResult result;
  const _AnswerCard({required this.result});

  @override
  Widget build(BuildContext context) {
    if (result.isUnsupported) return _UnsupportedCard(result: result);

    return Container(
      decoration: BoxDecoration(
        color: AppColors.surface,
        border: Border.all(color: AppColors.border),
        borderRadius: BorderRadius.circular(AppRadius.card),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _Understanding(result: result),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
            child: _AnswerBody(result: result),
          ),
          _AnswerFooter(result: result),
        ],
      ),
    );
  }
}

/// "Anladığım sorgu" şeridi + kullanılan veri setleri.
class _Understanding extends StatelessWidget {
  final AskResult result;
  const _Understanding({required this.result});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Icon(Icons.psychology_outlined, size: 17, color: AppColors.accent),
              const SizedBox(width: 10),
              Expanded(
                child: Text(
                  result.summary,
                  style: const TextStyle(
                      fontSize: 13, color: AppColors.muted, height: 1.45),
                ),
              ),
            ],
          ),
          if (result.datasets.isNotEmpty) ...[
            const SizedBox(height: 10),
            Wrap(
              spacing: 6,
              runSpacing: 6,
              children: [
                for (final d in result.datasets)
                  _Tag(text: d, icon: Icons.table_chart_outlined),
                // Birden çok set varsa birleştirme yapıldığını açıkça söyle: sonucun
                // nereden geldiğini anlamak için kritik.
                if (result.datasets.length > 1)
                  const _Tag(text: 'birleştirildi', icon: Icons.link, accent: true),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _AnswerBody extends StatelessWidget {
  final AskResult result;
  const _AnswerBody({required this.result});

  @override
  Widget build(BuildContext context) {
    final comparison = result.comparison;
    if (comparison != null) return _ComparisonView(comparison: comparison);

    final aggregate = result.aggregate;
    if (aggregate != null) return _AggregateView(aggregate: aggregate);

    final rows = result.rows;
    if (rows != null) return _RowsView(rows: rows);

    return const Text('Sonuç yok.', style: TextStyle(color: AppColors.muted));
  }
}

/// Gruplamasız sonuç → iri rakam(lar). Gruplu sonuç → grafik + tablo.
class _AggregateView extends StatelessWidget {
  final AskAggregate aggregate;
  const _AggregateView({required this.aggregate});

  @override
  Widget build(BuildContext context) {
    if (aggregate.buckets.isEmpty) {
      return const Text('Bu koşullara uyan kayıt bulunamadı.',
          style: TextStyle(color: AppColors.muted));
    }

    if (aggregate.isSingleValue) {
      final bucket = aggregate.buckets.first;

      // Gruplamasız sorgu koşullara uyan satır bulamasa bile TEK SATIR döner; içindeki
      // toplam NULL olur. Bunu "—" diye göstermek kullanıcıya hiçbir şey anlatmaz —
      // "hesaplanamadı mı, sıfır mı, hata mı?" belirsiz kalır. Durumu adıyla söylüyoruz.
      if (bucket.count == 0) {
        return const Text('Bu koşullara uyan kayıt bulunamadı.',
            style: TextStyle(color: AppColors.muted));
      }

      return Wrap(
        spacing: 12,
        runSpacing: 12,
        children: [
          for (var i = 0; i < aggregate.metrics.length; i++)
            _BigNumber(
              label: aggregate.metrics[i].label,
              value: _fmt(i < bucket.values.length ? bucket.values[i] : null),
            ),
        ],
      );
    }

    // Tek grup kaldıysa grafik anlamsız (tek çubuk) — düz cevap daha okunur.
    if (aggregate.buckets.length == 1 && aggregate.metrics.length == 1) {
      final b = aggregate.buckets.first;
      return _SingleAnswer(
        label: '${b.label} · ${aggregate.metrics.first.label}',
        value: _fmt(b.values.isEmpty ? null : b.values.first),
      );
    }

    final data = [
      for (final b in aggregate.buckets)
        ChartDatum(b.label, b.values.isEmpty ? 0 : (b.values.first ?? 0)),
    ];

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // Zaman serisinde çizgi, kategoride çubuk: aynı veri farklı soruların cevabı.
        if (aggregate.bucket != null)
          AppLineChart(data: data, valueLabel: aggregate.metrics.firstOrNull?.label)
        else
          AppBarChart(data: data, valueLabel: aggregate.metrics.firstOrNull?.label),
        const SizedBox(height: 16),
        _AggregateTable(aggregate: aggregate),
      ],
    );
  }
}

class _AggregateTable extends StatelessWidget {
  final AskAggregate aggregate;
  const _AggregateTable({required this.aggregate});

  @override
  Widget build(BuildContext context) {
    final hasShare = aggregate.buckets.any((b) => b.share != null);

    return _ScrollableTable(
      columns: [
        DataColumn(label: Text(aggregate.groupBy.join(' · '))),
        for (final m in aggregate.metrics) DataColumn(label: Text(m.label), numeric: true),
        if (hasShare) const DataColumn(label: Text('Pay'), numeric: true),
        const DataColumn(label: Text('Kayıt'), numeric: true),
      ],
      rows: [
        for (final b in aggregate.buckets)
          DataRow(cells: [
            DataCell(Text(b.label)),
            for (var i = 0; i < aggregate.metrics.length; i++)
              DataCell(Text(_fmt(i < b.values.length ? b.values[i] : null))),
            if (hasShare)
              DataCell(Text(b.share == null ? '—' : '%${b.share!.toStringAsFixed(1)}')),
            DataCell(Text('${b.count}')),
          ]),
      ],
    );
  }
}

/// Tablo HER ZAMAN doğru sunum değil.
///
/// "En pahalı satışı yapan müşterinin adı" sorusunun cevabı tek bir isimdir; onu üç
/// sütunlu bir ızgaraya sokmak cevabı gizler. Karar sonucun ŞEKLİNE göre veriliyor:
/// tek hücre → düz cevap, tek satır → etiketli değerler, çok satır → tablo.
class _RowsView extends StatelessWidget {
  final AskRows rows;
  const _RowsView({required this.rows});

  @override
  Widget build(BuildContext context) {
    if (rows.rows.isEmpty) {
      return const Text('Bu koşullara uyan kayıt bulunamadı.',
          style: TextStyle(color: AppColors.muted));
    }

    final singleRow = rows.rows.length == 1;

    if (singleRow && rows.columns.length == 1) {
      return _SingleAnswer(label: rows.columns.first, value: rows.rows.first.first ?? '—');
    }

    // Tek satır ama birkaç kolon: yine tablo değil, okunur bir etiket-değer listesi.
    if (singleRow && rows.columns.length <= 4) {
      return Wrap(
        spacing: 12,
        runSpacing: 12,
        children: [
          for (var i = 0; i < rows.columns.length; i++)
            _SingleAnswer(
              label: rows.columns[i],
              value: rows.rows.first[i] ?? '—',
              compact: true,
            ),
        ],
      );
    }

    return _ScrollableTable(
      columns: [for (final c in rows.columns) DataColumn(label: Text(c))],
      rows: [
        for (final r in rows.rows)
          DataRow(cells: [for (final v in r) DataCell(Text(v ?? '—'))]),
      ],
    );
  }
}

/// Tek bir cevabın gösterimi: küçük etiket, iri değer.
class _SingleAnswer extends StatelessWidget {
  final String label;
  final String value;
  final bool compact;

  const _SingleAnswer({required this.label, required this.value, this.compact = false});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: EdgeInsets.symmetric(horizontal: compact ? 16 : 20, vertical: compact ? 12 : 16),
      decoration: BoxDecoration(
        color: AppColors.bg,
        border: Border.all(color: AppColors.border),
        borderRadius: BorderRadius.circular(AppRadius.control),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(label.toUpperCase(),
              style: const TextStyle(
                  fontSize: 10.5,
                  letterSpacing: 0.6,
                  fontWeight: FontWeight.w700,
                  color: AppColors.muted)),
          const SizedBox(height: 6),
          SelectableText(value,
              style: TextStyle(
                  fontSize: compact ? 19 : 27,
                  fontWeight: FontWeight.w700,
                  letterSpacing: -0.5,
                  color: AppColors.text)),
        ],
      ),
    );
  }
}

/// Dönem karşılaştırma: değişim yönü hem renk hem okla gösterilir (yalnız renk,
/// renk körü kullanıcı için bilgi taşımaz).
class _ComparisonView extends StatelessWidget {
  final AskComparison comparison;
  const _ComparisonView({required this.comparison});

  @override
  Widget build(BuildContext context) {
    if (comparison.rows.isEmpty) {
      return const Text('Karşılaştırılacak kayıt bulunamadı.',
          style: TextStyle(color: AppColors.muted));
    }

    return _ScrollableTable(
      columns: const [
        DataColumn(label: Text('Grup')),
        DataColumn(label: Text('Bu dönem'), numeric: true),
        DataColumn(label: Text('Önceki'), numeric: true),
        DataColumn(label: Text('Değişim'), numeric: true),
      ],
      rows: [
        for (final r in comparison.rows)
          DataRow(cells: [
            DataCell(Text(r.key?.isNotEmpty == true ? r.key! : 'Toplam')),
            DataCell(Text(_fmt(r.current))),
            DataCell(Text(r.previous == null ? 'yok' : _fmt(r.previous))),
            DataCell(_Delta(row: r)),
          ]),
      ],
    );
  }
}

class _Delta extends StatelessWidget {
  final AskComparisonRow row;
  const _Delta({required this.row});

  @override
  Widget build(BuildContext context) {
    final pct = row.deltaPercent;

    // Önceki dönem yoksa ya da sıfırsa yüzde tanımsızdır. "%100 artış" yazmak
    // uydurma olurdu; bunun yerine durumu adıyla söylüyoruz.
    if (pct == null) {
      return Text(row.previous == null ? 'yeni' : '—',
          style: const TextStyle(fontSize: 12.5, color: AppColors.muted));
    }

    final up = pct >= 0;
    final color = up ? AppColors.accent : AppColors.danger;

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(up ? Icons.arrow_upward : Icons.arrow_downward, size: 13, color: color),
        const SizedBox(width: 4),
        Text('%${pct.abs().toStringAsFixed(1)}',
            style: TextStyle(fontSize: 12.5, color: color, fontWeight: FontWeight.w600)),
      ],
    );
  }
}

class _UnsupportedCard extends StatelessWidget {
  final AskResult result;
  const _UnsupportedCard({required this.result});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppColors.warning.withValues(alpha: 0.07),
        border: Border.all(color: AppColors.warning.withValues(alpha: 0.32)),
        borderRadius: BorderRadius.circular(AppRadius.card),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(Icons.info_outline, size: 19, color: AppColors.warning),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Text('Bu soruyu cevaplayamıyorum',
                    style: TextStyle(fontSize: 14, fontWeight: FontWeight.w600)),
                const SizedBox(height: 6),
                Text(result.reason ?? '',
                    style: const TextStyle(fontSize: 13, height: 1.45)),
                const SizedBox(height: 10),
                const Text(
                  'Soru kaydedildi. Sık istenen yetenekler sonraki sürümlere ekleniyor.',
                  style: TextStyle(fontSize: 12, color: AppColors.muted),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// Alt şerit: hangi model cevapladı ve ne kadar sürdü.
///
/// Üretilen SQL bilinçli olarak GÖSTERİLMİYOR. İş kullanıcısı SQL okumaz; okuyamadığı
/// bir şeyi göstermek güven vermez. Modelin soruyu doğru anlayıp anlamadığını görmesi
/// için kartın başındaki "anladığım sorgu" satırı var — müşteriye dönük doğrulama
/// aracı odur. SQL sunucu loglarında ve API yanıtında duruyor (hata ayıklama için).
class _AnswerFooter extends StatelessWidget {
  final AskResult result;
  const _AnswerFooter({required this.result});

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        color: AppColors.bg,
        border: Border(top: BorderSide(color: AppColors.border)),
        borderRadius: BorderRadius.only(
          bottomLeft: Radius.circular(AppRadius.card),
          bottomRight: Radius.circular(AppRadius.card),
        ),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 9),
      child: Row(
        children: [
          const Icon(Icons.memory, size: 13, color: AppColors.muted),
          const SizedBox(width: 6),
          Flexible(
            child: Text(result.model,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(fontSize: 11.5, color: AppColors.muted)),
          ),
          const SizedBox(width: 14),
          const Icon(Icons.schedule, size: 13, color: AppColors.muted),
          const SizedBox(width: 6),
          Text('${result.planMs} ms + ${result.queryMs} ms',
              style: const TextStyle(fontSize: 11.5, color: AppColors.muted)),
        ],
      ),
    );
  }
}

// --- soru kutusu --------------------------------------------------------------------

class _Composer extends StatelessWidget {
  final TextEditingController controller;
  final FocusNode focusNode;
  final bool sending;
  final VoidCallback onSend;

  const _Composer({
    required this.controller,
    required this.focusNode,
    required this.sending,
    required this.onSend,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.fromLTRB(6, 6, 6, 6),
      decoration: BoxDecoration(
        color: AppColors.surface,
        border: Border.all(color: AppColors.border),
        borderRadius: BorderRadius.circular(AppRadius.card),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Expanded(
            // Enter gönderir, Shift+Enter satır atlar — sohbet arayüzlerinin alışılmış
            // davranışı; kullanıcı göndermek için fareye uzanmak zorunda kalmıyor.
            child: Shortcuts(
              shortcuts: const {
                SingleActivator(LogicalKeyboardKey.enter): _SendIntent(),
              },
              child: Actions(
                actions: {
                  _SendIntent: CallbackAction<_SendIntent>(
                    onInvoke: (_) {
                      onSend();
                      return null;
                    },
                  ),
                },
                child: TextField(
                  controller: controller,
                  focusNode: focusNode,
                  enabled: !sending,
                  maxLines: 4,
                  minLines: 1,
                  textInputAction: TextInputAction.newline,
                  style: const TextStyle(fontSize: 14),
                  decoration: const InputDecoration(
                    hintText: 'Modele bir soru sor…',
                    filled: false,
                    border: InputBorder.none,
                    enabledBorder: InputBorder.none,
                    focusedBorder: InputBorder.none,
                    contentPadding: EdgeInsets.symmetric(horizontal: 14, vertical: 12),
                  ),
                ),
              ),
            ),
          ),
          const SizedBox(width: 6),
          SizedBox(
            width: 42,
            height: 42,
            child: FilledButton(
              onPressed: sending ? null : onSend,
              style: FilledButton.styleFrom(
                padding: EdgeInsets.zero,
                shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(AppRadius.control)),
              ),
              child: sending
                  ? const ButtonSpinner()
                  : const Icon(Icons.arrow_upward, size: 19),
            ),
          ),
        ],
      ),
    );
  }
}

class _SendIntent extends Intent {
  const _SendIntent();
}

// --- geçmiş sohbetler ---------------------------------------------------------------

/// Kullanıcının kendi sohbetleri. Seçilen sohbetin kimliğini döndürür.
class _HistoryDialog extends StatefulWidget {
  const _HistoryDialog();

  @override
  State<_HistoryDialog> createState() => _HistoryDialogState();
}

class _HistoryDialogState extends State<_HistoryDialog> {
  late Future<List<ChatSummary>> _future;

  @override
  void initState() {
    super.initState();
    _future = ApiService.conversations();
  }

  Future<void> _delete(ChatSummary chat) async {
    try {
      await ApiService.deleteConversation(chat.id);
      if (!mounted) return;
      // Future'ı setState İÇİNDE kurma: içerideki istisna setState'i patlatır.
      final refreshed = ApiService.conversations();
      setState(() => _future = refreshed);
    } catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(e.toString())));
    }
  }

  @override
  Widget build(BuildContext context) {
    return Dialog(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480, maxHeight: 520),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 18, 12, 12),
              child: Row(
                children: [
                  Expanded(
                    child: Text('Geçmiş sohbetler',
                        style: Theme.of(context).textTheme.titleLarge),
                  ),
                  IconButton(
                    onPressed: () => Navigator.pop(context),
                    icon: const Icon(Icons.close, size: 20),
                  ),
                ],
              ),
            ),
            const Divider(height: 1),
            Flexible(
              child: FutureBuilder<List<ChatSummary>>(
                future: _future,
                builder: (context, snapshot) {
                  if (snapshot.connectionState != ConnectionState.done) {
                    return const Padding(
                      padding: EdgeInsets.all(40),
                      child: Center(child: CircularProgressIndicator()),
                    );
                  }

                  if (snapshot.hasError) {
                    return Padding(
                      padding: const EdgeInsets.all(24),
                      child: Text('${snapshot.error}',
                          style: const TextStyle(color: AppColors.danger)),
                    );
                  }

                  final chats = snapshot.data ?? [];
                  if (chats.isEmpty) {
                    return const Padding(
                      padding: EdgeInsets.all(36),
                      child: Text(
                        'Henüz kayıtlı sohbet yok.\nSorduğun her soru buraya kaydedilir.',
                        textAlign: TextAlign.center,
                        style: TextStyle(color: AppColors.muted, height: 1.5),
                      ),
                    );
                  }

                  return ListView.separated(
                    shrinkWrap: true,
                    padding: const EdgeInsets.symmetric(vertical: 6),
                    itemCount: chats.length,
                    separatorBuilder: (_, _) => const Divider(height: 1),
                    itemBuilder: (_, i) => _HistoryRow(
                      chat: chats[i],
                      onOpen: () => Navigator.pop(context, chats[i].id),
                      onDelete: () => _delete(chats[i]),
                    ),
                  );
                },
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _HistoryRow extends StatelessWidget {
  final ChatSummary chat;
  final VoidCallback onOpen;
  final VoidCallback onDelete;

  const _HistoryRow({
    required this.chat,
    required this.onOpen,
    required this.onDelete,
  });

  @override
  Widget build(BuildContext context) {
    return ListTile(
      onTap: onOpen,
      title: Text(chat.title,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: const TextStyle(fontSize: 13.5)),
      subtitle: Text(
        '${chat.messageCount} soru · ${_ago(chat.updatedAt)}',
        style: const TextStyle(fontSize: 11.5, color: AppColors.muted),
      ),
      trailing: IconButton(
        onPressed: onDelete,
        icon: const Icon(Icons.delete_outline, size: 18),
        tooltip: 'Sohbeti sil',
      ),
    );
  }

  // "3 saat önce" gibi göreli zaman: tam tarih listede gereksiz gürültü yaratır.
  static String _ago(DateTime time) {
    final diff = DateTime.now().difference(time.toLocal());
    if (diff.inMinutes < 1) return 'az önce';
    if (diff.inMinutes < 60) return '${diff.inMinutes} dakika önce';
    if (diff.inHours < 24) return '${diff.inHours} saat önce';
    if (diff.inDays < 30) return '${diff.inDays} gün önce';
    return '${time.toLocal().day}.${time.toLocal().month}.${time.toLocal().year}';
  }
}

// --- küçük parçalar -----------------------------------------------------------------

class _Tag extends StatelessWidget {
  final String text;
  final IconData? icon;
  final bool accent;

  const _Tag({required this.text, this.icon, this.accent = false});

  @override
  Widget build(BuildContext context) {
    final color = accent ? AppColors.accent : AppColors.muted;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.10),
        border: Border.all(color: color.withValues(alpha: 0.3)),
        borderRadius: BorderRadius.circular(999),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[
            Icon(icon, size: 12, color: color),
            const SizedBox(width: 5),
          ],
          Text(text,
              style: TextStyle(fontSize: 11.5, color: color, fontWeight: FontWeight.w600)),
        ],
      ),
    );
  }
}

class _BigNumber extends StatelessWidget {
  final String label;
  final String value;

  const _BigNumber({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 14),
      decoration: BoxDecoration(
        color: AppColors.bg,
        border: Border.all(color: AppColors.border),
        borderRadius: BorderRadius.circular(AppRadius.control),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label.toUpperCase(),
              style: const TextStyle(
                  fontSize: 10.5,
                  letterSpacing: 0.6,
                  fontWeight: FontWeight.w700,
                  color: AppColors.muted)),
          const SizedBox(height: 6),
          Text(value,
              style: const TextStyle(
                  fontSize: 26,
                  fontWeight: FontWeight.w700,
                  letterSpacing: -0.5,
                  color: AppColors.text)),
        ],
      ),
    );
  }
}

/// Geniş tablolar kendi içinde yatay kaydırılır; sayfa gövdesi yana kaymaz.
class _ScrollableTable extends StatelessWidget {
  final List<DataColumn> columns;
  final List<DataRow> rows;

  const _ScrollableTable({required this.columns, required this.rows});

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        border: Border.all(color: AppColors.border),
        borderRadius: BorderRadius.circular(AppRadius.control),
      ),
      clipBehavior: Clip.antiAlias,
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: DataTable(columns: columns, rows: rows),
      ),
    );
  }
}

// Sayı biçimi: tam sayıysa ondalık gösterme, değilse iki hane. Binlik ayracı Türkçe.
String _fmt(double? v) {
  if (v == null) return '—';
  final fixed = v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(2);
  final parts = fixed.split('.');
  final digits = parts[0].replaceAllMapped(
    RegExp(r'(\d)(?=(\d{3})+$)'),
    (m) => '${m[1]}.',
  );
  return parts.length > 1 ? '$digits,${parts[1]}' : digits;
}
