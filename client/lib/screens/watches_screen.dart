import 'package:flutter/material.dart';

import '../api_service.dart';
import '../theme/app_theme.dart';
import '../widgets/charts.dart';
import '../widgets/ui.dart';

// İzleyiciler — sistemin kendiliğinden konuştuğu yer.
//
// Bugüne kadarki her ekran ÇEKME üzerine kuruluydu: kullanıcı sorar, sistem cevaplar.
// Burası tersi; soru bir kez sorulur, cevabını sistem takip eder ve eşik geçilince
// kendisi haber verir. Bu yüzden ekranın merkezinde "sorgu kur" formu değil, kurulmuş
// izleyicilerin DURUMU var: hangisi çalışıyor, hangisi eşiği aştı, hangisi kırıldı.
//
// İzleyici FİRMAYA ait (sohbetin aksine): listede başkasının kurduğu izleyiciler de
// görünür, "kuran" bilgisi sahiplik değil izdir.

/// Durumun rengi. Kırık = tehlike, aşıldı = uyarı, çalışıyor = turkuaz.
/// Kapalı izleyici sönük görünür: susturulmuş bir alarmın çalışan bir alarmla aynı
/// görünmesi, kullanıcının kapalı olduğunu fark etmemesi demek olurdu.
Color _statusColor(Watch w) {
  if (!w.isEnabled) return AppColors.muted;
  if (w.isBroken) return AppColors.danger;
  if (w.isBreaching) return AppColors.warning;
  return AppColors.accent;
}

String _statusLabel(Watch w) {
  if (!w.isEnabled) return 'kapalı';
  if (w.isBroken) return 'kırık';
  if (w.isBreaching) return 'eşik aşıldı';
  return 'izliyor';
}

IconData _statusIcon(Watch w) {
  if (!w.isEnabled) return Icons.pause_circle_outline;
  if (w.isBroken) return Icons.error_outline;
  if (w.isBreaching) return Icons.notifications_active_outlined;
  return Icons.visibility_outlined;
}

/// Koşulun okunur hâli: "Değer 1.000'den küçükse" / "Değişim %20'den büyükse".
String watchConditionLabel(
    String conditionKind, String op, double threshold) {
  final isChange = conditionKind == 'change';
  final subject = isChange ? 'Değişim' : 'Değer';
  final value = isChange ? '%${formatNumber(threshold)}' : formatNumber(threshold);
  final relation = switch (op) {
    'gt' => '$value üzerine çıkarsa',
    'gte' => '$value ya da üzerine çıkarsa',
    'lt' => '$value altına inerse',
    'lte' => '$value ya da altına inerse',
    _ => '$value ile karşılaştırılır',
  };
  return '$subject $relation';
}

/// Aynı koşulun dar yerlere sığan hâli: "> 1.000", "< %20".
///
/// Kart üzerindeki cümle okunur olsun diye uzun; özet kutusunda ise kırpılıp
/// "Değişim %20 üzerine ..." gibi yarım kalıyordu — yarım okunan bir koşul, yanlış
/// okunan bir koşuldur.
String watchConditionShort(String conditionKind, String op, double threshold) {
  final value = conditionKind == 'change'
      ? '%${formatNumber(threshold)}'
      : formatNumber(threshold);
  final symbol = switch (op) {
    'gt' => '>',
    'gte' => '≥',
    'lt' => '<',
    'lte' => '≤',
    _ => '=',
  };
  return '$symbol $value';
}

class WatchesPage extends StatefulWidget {
  /// Uyarıya tıklanarak açıldıysa doğrudan o izleyicinin ayrıntısı gösterilir.
  final String? initialWatchId;

  /// Okunmamış rozeti kabukta duruyor; burada bir şey okununca haber veriliyor.
  final VoidCallback? onAlertsChanged;

  const WatchesPage({super.key, this.initialWatchId, this.onAlertsChanged});

  @override
  State<WatchesPage> createState() => _WatchesPageState();
}

class _WatchesPageState extends State<WatchesPage> {
  late Future<List<Watch>> _future;
  String? _openId;

  @override
  void initState() {
    super.initState();
    _openId = widget.initialWatchId;
    _future = ApiService.watches();
  }

  @override
  void didUpdateWidget(WatchesPage oldWidget) {
    super.didUpdateWidget(oldWidget);
    // Bildirime tıklanınca kabuk aynı sayfayı yeni bir kimlikle çiziyor.
    if (widget.initialWatchId != null &&
        widget.initialWatchId != oldWidget.initialWatchId) {
      setState(() => _openId = widget.initialWatchId);
    }
  }

  // Future setState'in DIŞINDA kuruluyor: içeride kurulursa istisna setState'i patlatır
  // ve ekran "silinemedi" gibi sahte hatalar gösterir.
  void _reload() {
    final refreshed = ApiService.watches();
    setState(() => _future = refreshed);
  }

  @override
  Widget build(BuildContext context) {
    final openId = _openId;
    if (openId != null) {
      return WatchDetailPage(
        watchId: openId,
        onBack: () {
          setState(() => _openId = null);
          _reload();
        },
        onChanged: _reload,
        onAlertsChanged: widget.onAlertsChanged,
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        PageHeader(
          leading: const IconBadge(
              icon: Icons.notifications_active_outlined, color: AppColors.warning),
          title: 'İzleyiciler',
          subtitle: 'Kaydedilmiş bir soru belirli aralıklarla kendiliğinden çalışır; '
              'sonuç eşiği geçtiğinde sistem haber verir. '
              'Yeni izleyici, sohbetteki bir cevabın altındaki "İzle" ile kurulur.',
          actions: [
            IconButton(
              onPressed: _reload,
              icon: const Icon(Icons.refresh, size: 20),
              tooltip: 'Yenile',
            ),
          ],
        ),
        Expanded(
          child: FutureBuilder<List<Watch>>(
            future: _future,
            builder: (context, snapshot) {
              if (snapshot.connectionState != ConnectionState.done) {
                return const LoadingView();
              }
              if (snapshot.hasError) {
                return ErrorView(message: '${snapshot.error}', onRetry: _reload);
              }

              final watches = snapshot.data ?? [];
              if (watches.isEmpty) {
                return const EmptyState(
                  icon: Icons.notifications_none,
                  title: 'Henüz izleyici yok',
                  message: 'Soru sor bölümünde bir cevap aldıktan sonra kartın altındaki '
                      '"İzle" düğmesine bas. Aynı soru bundan sonra belirli aralıklarla '
                      'kendiliğinden çalışır ve eşiği geçtiğinde haber verilir.',
                );
              }

              return ListView.separated(
                itemCount: watches.length,
                separatorBuilder: (_, _) => const SizedBox(height: 10),
                itemBuilder: (_, i) => _WatchCard(
                  watch: watches[i],
                  onOpen: () => setState(() => _openId = watches[i].id),
                  onChanged: _reload,
                ),
              );
            },
          ),
        ),
      ],
    );
  }
}

// --- liste satırı ---------------------------------------------------------------------

class _WatchCard extends StatelessWidget {
  final Watch watch;
  final VoidCallback onOpen;
  final VoidCallback onChanged;

  const _WatchCard({
    required this.watch,
    required this.onOpen,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    final color = _statusColor(watch);

    return Material(
      color: AppColors.surface,
      borderRadius: BorderRadius.circular(AppRadius.card),
      child: InkWell(
        onTap: onOpen,
        borderRadius: BorderRadius.circular(AppRadius.card),
        child: Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            border: Border.all(color: AppColors.border),
            borderRadius: BorderRadius.circular(AppRadius.card),
          ),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              IconBadge(icon: _statusIcon(watch), color: color),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Flexible(
                          child: Text(
                            watch.title,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: const TextStyle(
                                fontSize: 14.5, fontWeight: FontWeight.w600),
                          ),
                        ),
                        const SizedBox(width: 8),
                        _StatusChip(watch: watch),
                        if (watch.unreadCount > 0) ...[
                          const SizedBox(width: 6),
                          _UnreadDot(count: watch.unreadCount),
                        ],
                      ],
                    ),
                    const SizedBox(height: 6),
                    Text(
                      '${watchConditionLabel(watch.conditionKind, watch.op, watch.threshold)}'
                      '  ·  ${watchIntervals[watch.intervalMinutes] ?? '${watch.intervalMinutes} dk'}',
                      style: const TextStyle(fontSize: 12.5, color: AppColors.muted),
                    ),
                    const SizedBox(height: 10),
                    Wrap(
                      spacing: 14,
                      runSpacing: 6,
                      crossAxisAlignment: WrapCrossAlignment.center,
                      children: [
                        _MiniStat(
                          label: 'son değer',
                          // Kırık izleyicide eski değer duruyor ama SÖNÜK: o sayı
                          // artık güncel değil, en son ne görüldüğünün kaydı.
                          value: formatNumber(watch.lastValue),
                          color: watch.isBroken ? AppColors.muted : AppColors.text,
                        ),
                        if (watch.lastRunAt != null)
                          _MiniStat(
                              label: 'son koşu', value: timeAgo(watch.lastRunAt!)),
                        if (watch.isEnabled && !watch.isBroken)
                          _MiniStat(
                              label: 'sıradaki', value: timeUntil(watch.nextRunAt)),
                        if (watch.createdBy.isNotEmpty)
                          _MiniStat(label: 'kuran', value: watch.createdBy),
                      ],
                    ),
                    if (watch.error != null) ...[
                      const SizedBox(height: 10),
                      _BrokenNote(error: watch.error!),
                    ],
                  ],
                ),
              ),
              const SizedBox(width: 8),
              _CardActions(watch: watch, onChanged: onChanged),
            ],
          ),
        ),
      ),
    );
  }
}

/// Kart üzerindeki hızlı eylemler. Yazma yetkisi olmayan (Viewer) kullanıcıda HİÇBİRİ
/// görünmez: bir izleyiciyi kapatmak ya da silmek firmanın tamamını etkiler.
class _CardActions extends StatelessWidget {
  final Watch watch;
  final VoidCallback onChanged;

  const _CardActions({required this.watch, required this.onChanged});

  @override
  Widget build(BuildContext context) {
    if (!ApiService.canWrite) return const SizedBox.shrink();

    return PopupMenuButton<String>(
      icon: const Icon(Icons.more_horiz, size: 20, color: AppColors.muted),
      tooltip: 'İşlemler',
      color: AppColors.surfaceAlt,
      onSelected: (action) => _run(context, action),
      itemBuilder: (_) => [
        const PopupMenuItem(
          value: 'run',
          child: _MenuRow(icon: Icons.play_arrow_outlined, label: 'Şimdi kontrol et'),
        ),
        const PopupMenuItem(
          value: 'edit',
          child: _MenuRow(icon: Icons.tune, label: 'Eşiği düzenle'),
        ),
        PopupMenuItem(
          value: 'toggle',
          child: _MenuRow(
            icon: watch.isEnabled ? Icons.pause_outlined : Icons.play_circle_outline,
            label: watch.isEnabled ? 'Duraklat' : 'Sürdür',
          ),
        ),
        const PopupMenuItem(
          value: 'delete',
          child: _MenuRow(
              icon: Icons.delete_outline, label: 'Sil', color: AppColors.danger),
        ),
      ],
    );
  }

  Future<void> _run(BuildContext context, String action) async {
    try {
      switch (action) {
        case 'run':
          final updated = await ApiService.runWatch(watch.id);
          if (!context.mounted) return;
          showSnack(context, _runMessage(updated));

        case 'edit':
          final saved = await showWatchThresholdDialog(context, watch: watch);
          if (saved != true) return;

        case 'toggle':
          await ApiService.updateWatch(watch.id, isEnabled: !watch.isEnabled);
          if (!context.mounted) return;
          showSnack(context,
              watch.isEnabled ? 'İzleyici duraklatıldı.' : 'İzleyici sürdürüldü.');

        case 'delete':
          final ok = await _confirmDelete(context);
          if (ok != true) return;
          await ApiService.deleteWatch(watch.id);
          if (!context.mounted) return;
          showSnack(context, 'İzleyici silindi.');
      }
      onChanged();
    } catch (e) {
      if (!context.mounted) return;
      showSnack(context, '$e', isError: true);
    }
  }

  Future<bool?> _confirmDelete(BuildContext context) => showDialog<bool>(
        context: context,
        builder: (_) => AlertDialog(
          title: const Text('İzleyici silinsin mi?'),
          content: Text(
            '"${watch.title}" izleyicisi ve değer geçmişi silinecek. '
            'Yalnız susturmak istiyorsan silmek yerine duraklatabilirsin.',
            style: const TextStyle(fontSize: 13, color: AppColors.muted, height: 1.5),
          ),
          actions: [
            TextButton(
                onPressed: () => Navigator.pop(context, false),
                child: const Text('Vazgeç')),
            FilledButton(
              style: FilledButton.styleFrom(backgroundColor: AppColors.danger),
              onPressed: () => Navigator.pop(context, true),
              child: const Text('Sil'),
            ),
          ],
        ),
      );
}

/// Elle çalıştırmanın sonucu tek cümlede söylenir: kullanıcı düğmeye "çalışıyor mu"
/// diye bastığı için cevabı da orada almalı.
String _runMessage(Watch w) {
  if (w.isBroken) return 'İzleyici çalışmadı: ${w.error ?? 'sebep bilinmiyor'}';
  final value = formatNumber(w.lastValue);
  return w.isBreaching
      ? 'Ölçülen değer: $value — eşiğin dışında.'
      : 'Ölçülen değer: $value — eşik aşılmadı.';
}

class _MenuRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final Color? color;

  const _MenuRow({required this.icon, required this.label, this.color});

  @override
  Widget build(BuildContext context) => Row(
        children: [
          Icon(icon, size: 17, color: color ?? AppColors.muted),
          const SizedBox(width: 10),
          Text(label, style: TextStyle(fontSize: 13, color: color)),
        ],
      );
}

class _StatusChip extends StatelessWidget {
  final Watch watch;
  const _StatusChip({required this.watch});

  @override
  Widget build(BuildContext context) {
    final color = _statusColor(watch);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.12),
        border: Border.all(color: color.withValues(alpha: 0.3)),
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(_statusLabel(watch),
          style: TextStyle(
              fontSize: 11, color: color, fontWeight: FontWeight.w600)),
    );
  }
}

class _UnreadDot extends StatelessWidget {
  final int count;
  const _UnreadDot({required this.count});

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
        decoration: BoxDecoration(
          color: AppColors.brand,
          borderRadius: BorderRadius.circular(999),
        ),
        child: Text('$count yeni',
            style: const TextStyle(
                fontSize: 10.5, color: Colors.white, fontWeight: FontWeight.w700)),
      );
}

class _MiniStat extends StatelessWidget {
  final String label;
  final String value;
  final Color color;

  const _MiniStat({
    required this.label,
    required this.value,
    this.color = AppColors.text,
  });

  @override
  Widget build(BuildContext context) => Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text('$label ',
              style: const TextStyle(fontSize: 11.5, color: AppColors.muted)),
          Text(value,
              style: TextStyle(
                  fontSize: 12.5, color: color, fontWeight: FontWeight.w600)),
        ],
      );
}

/// Kırık izleyicinin sebebi. Görünür yerde duruyor: kırık bir alarmın en tehlikeli hâli
/// çalışıyor sanılmasıdır.
class _BrokenNote extends StatelessWidget {
  final String error;
  const _BrokenNote({required this.error});

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
        decoration: BoxDecoration(
          color: AppColors.danger.withValues(alpha: 0.08),
          border: Border.all(color: AppColors.danger.withValues(alpha: 0.25)),
          borderRadius: BorderRadius.circular(AppRadius.small),
        ),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Icon(Icons.link_off, size: 15, color: AppColors.danger),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                'Bu izleyici çalışmıyor: $error',
                style: const TextStyle(
                    fontSize: 12, color: AppColors.danger, height: 1.4),
              ),
            ),
          ],
        ),
      );
}

// --- ayrıntı --------------------------------------------------------------------------

/// Tek bir izleyicinin ayrıntısı: ne ölçtüğü, değer geçmişi ve koşu kayıtları.
///
/// Ekranın merkezinde GEÇMİŞ var, anlık değer değil: bir eşiğin aşılıp aşılmadığından
/// çok, değerin nereye doğru gittiği ilgilendiriyor.
class WatchDetailPage extends StatefulWidget {
  final String watchId;
  final VoidCallback onBack;
  final VoidCallback onChanged;
  final VoidCallback? onAlertsChanged;

  const WatchDetailPage({
    super.key,
    required this.watchId,
    required this.onBack,
    required this.onChanged,
    this.onAlertsChanged,
  });

  @override
  State<WatchDetailPage> createState() => _WatchDetailPageState();
}

class _WatchDetailPageState extends State<WatchDetailPage> {
  late Future<WatchDetail> _future;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  /// Ayrıntı açılınca bu izleyicinin uyarıları okundu sayılıyor: kullanıcı zaten
  /// karşısında duruyor, rozetin orada kalması gereksiz gürültü olurdu.
  Future<WatchDetail> _load() async {
    final detail = await ApiService.watch(widget.watchId);

    final unread = detail.runs
        .where((r) => r.notified && r.readAt == null)
        .map((r) => r.id)
        .toList();

    if (unread.isNotEmpty) {
      try {
        await ApiService.markWatchAlertsRead(runIds: unread);
        widget.onAlertsChanged?.call();
      } catch (_) {
        // Okundu işaretlemek görüntüleme durumudur; başarısız olması ekranı
        // kapatmamalı — kullanıcı yine de izleyiciyi görebilmeli.
      }
    }

    return detail;
  }

  void _reload() {
    final refreshed = _load();
    setState(() => _future = refreshed);
    widget.onChanged();
  }

  Future<void> _runNow() async {
    setState(() => _busy = true);
    try {
      final updated = await ApiService.runWatch(widget.watchId);
      if (!mounted) return;
      showSnack(context, _runMessage(updated));
      _reload();
    } catch (e) {
      if (!mounted) return;
      showSnack(context, '$e', isError: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _edit(Watch watch) async {
    final saved = await showWatchThresholdDialog(context, watch: watch);
    if (saved == true) _reload();
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<WatchDetail>(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const LoadingView();
        }
        if (snapshot.hasError) {
          return ErrorView(message: '${snapshot.error}', onRetry: _reload);
        }

        final detail = snapshot.data!;
        final watch = detail.watch;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            PageHeader(
              leading: IconButton(
                onPressed: widget.onBack,
                icon: const Icon(Icons.arrow_back, size: 20),
                tooltip: 'İzleyiciler',
              ),
              title: watch.title,
              subtitle: watch.question,
              actions: [
                if (ApiService.canWrite) ...[
                  OutlinedButton.icon(
                    onPressed: _busy ? null : _runNow,
                    icon: _busy
                        ? const SizedBox(
                            width: 14,
                            height: 14,
                            child: CircularProgressIndicator(strokeWidth: 2))
                        : const Icon(Icons.play_arrow_outlined, size: 18),
                    label: const Text('Şimdi kontrol et'),
                  ),
                  const SizedBox(width: 10),
                  FilledButton.icon(
                    onPressed: () => _edit(watch),
                    icon: const Icon(Icons.tune, size: 18),
                    label: const Text('Eşiği düzenle'),
                  ),
                ],
              ],
            ),
            Expanded(
              child: ListView(
                children: [
                  if (watch.error != null) ...[
                    _BrokenNote(error: watch.error!),
                    const SizedBox(height: 14),
                  ],
                  _DetailStats(watch: watch),
                  const SizedBox(height: 14),
                  SectionCard(
                    title: 'Şöyle anladım',
                    subtitle: 'Bu izleyici her koşuda aynı sorguyu çalıştırır; '
                        'soru bir daha modele sorulmaz.',
                    child: Text(
                      detail.summary.isEmpty ? watch.question : detail.summary,
                      style: const TextStyle(
                          fontSize: 13, color: AppColors.muted, height: 1.5),
                    ),
                  ),
                  const SizedBox(height: 14),
                  SectionCard(
                    title: 'Değer geçmişi',
                    subtitle: 'Ölçülemeyen koşular boşluk olarak görünür — '
                        '"ölçemedik" ile "sıfır ölçtük" aynı şey değil.',
                    child: AppHistoryChart(
                      labels: [
                        for (final r in detail.runs) _shortTime(r.ranAt),
                      ],
                      values: [for (final r in detail.runs) r.value],
                      breached: [for (final r in detail.runs) r.breached],
                      // Yüzde değişim izleyen bir izleyicide eşik, değerle aynı
                      // birimde değil; çizilseydi yanlış bir karşılaştırma olurdu.
                      threshold:
                          watch.conditionKind == 'value' ? watch.threshold : null,
                    ),
                  ),
                  const SizedBox(height: 14),
                  SectionCard(
                    title: 'Koşular',
                    subtitle: 'En son ${detail.runs.length} koşu, yeniden eskiye.',
                    child: _RunList(runs: detail.runs.reversed.toList()),
                  ),
                ],
              ),
            ),
          ],
        );
      },
    );
  }
}

class _DetailStats extends StatelessWidget {
  final Watch watch;
  const _DetailStats({required this.watch});

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        // Dar pencerede kartlar alt alta düşsün: sabit dört sütun 50 px taşırıyordu.
        final narrow = constraints.maxWidth < 720;
        final width = narrow
            ? constraints.maxWidth
            : (constraints.maxWidth - 3 * 12) / 4;

        final tiles = [
          StatTile(
            label: 'Son değer',
            value: formatNumber(watch.lastValue),
            hint: watch.lastRunAt == null
                ? 'henüz koşmadı'
                : timeAgo(watch.lastRunAt!),
            icon: Icons.speed,
            color: _statusColor(watch),
          ),
          StatTile(
            label: watch.conditionKind == 'change' ? 'Değişim eşiği' : 'Değer eşiği',
            value:
                watchConditionShort(watch.conditionKind, watch.op, watch.threshold),
            hint: watchIntervals[watch.intervalMinutes] ??
                '${watch.intervalMinutes} dakikada bir',
            icon: Icons.rule,
            color: AppColors.brand,
          ),
          StatTile(
            label: 'Durum',
            value: _statusLabel(watch),
            hint: watch.lastTriggeredAt == null
                ? 'henüz uyarı vermedi'
                : 'son uyarı ${timeAgo(watch.lastTriggeredAt!)}',
            icon: _statusIcon(watch),
            color: _statusColor(watch),
          ),
          StatTile(
            label: 'Sıradaki koşu',
            value: watch.isEnabled && !watch.isBroken
                ? timeUntil(watch.nextRunAt)
                : '—',
            hint: watch.isEnabled ? 'kuran: ${watch.createdBy}' : 'duraklatıldı',
            icon: Icons.schedule,
            color: AppColors.accent,
          ),
        ];

        return Wrap(
          spacing: 12,
          runSpacing: 12,
          children: [
            for (final tile in tiles) SizedBox(width: width, child: tile),
          ],
        );
      },
    );
  }
}

/// Koşu kayıtları. Uyarı doğuran koşular işaretli: kenar tetikleme yüzünden eşiğin
/// dışında olan HER koşu uyarı doğurmaz, yalnız duruma GEÇEN koşu doğurur.
class _RunList extends StatelessWidget {
  final List<WatchRun> runs;
  const _RunList({required this.runs});

  @override
  Widget build(BuildContext context) {
    if (runs.isEmpty) {
      return const Text('Henüz koşu yok.',
          style: TextStyle(fontSize: 13, color: AppColors.muted));
    }

    return Column(
      children: [
        for (var i = 0; i < runs.length; i++) ...[
          if (i > 0) const Divider(height: 1, color: AppColors.border),
          _RunRow(run: runs[i]),
        ],
      ],
    );
  }
}

class _RunRow extends StatelessWidget {
  final WatchRun run;
  const _RunRow({required this.run});

  @override
  Widget build(BuildContext context) {
    final failed = run.error != null;
    final color = failed
        ? AppColors.danger
        : run.breached
            ? AppColors.warning
            : AppColors.muted;

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 10),
      child: Row(
        children: [
          Icon(
            failed
                ? Icons.error_outline
                : run.breached
                    ? Icons.priority_high
                    : Icons.check,
            size: 16,
            color: color,
          ),
          const SizedBox(width: 12),
          SizedBox(
            width: 150,
            child: Text(_fullTime(run.ranAt),
                style: const TextStyle(fontSize: 12.5, color: AppColors.muted)),
          ),
          Expanded(
            child: failed
                ? Text(run.error!,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style:
                        const TextStyle(fontSize: 12.5, color: AppColors.danger))
                : Text(
                    // Ölçülemeyen koşuda tire var, sıfır YOK: uydurulmuş bir sayı
                    // geçmişi okuyan kişiyi yanıltırdı.
                    formatNumber(run.value),
                    style: TextStyle(
                        fontSize: 13.5,
                        fontWeight: FontWeight.w600,
                        color: run.breached ? AppColors.warning : AppColors.text),
                  ),
          ),
          if (run.notified)
            const Tooltip(
              message: 'Bu koşu uyarı gönderdi',
              child: Icon(Icons.notifications_active_outlined,
                  size: 15, color: AppColors.brand),
            ),
        ],
      ),
    );
  }
}

String _two(int v) => v.toString().padLeft(2, '0');

/// Grafik ekseni için kısa an: aynı gün içindeyse saat, değilse gün/ay.
String _shortTime(DateTime time) {
  final t = time.toLocal();
  final sameDay = DateTime.now().difference(t).inHours < 24;
  return sameDay ? '${_two(t.hour)}:${_two(t.minute)}' : '${t.day}.${t.month}';
}

String _fullTime(DateTime time) {
  final t = time.toLocal();
  return '${_two(t.day)}.${_two(t.month)}.${t.year}  ${_two(t.hour)}:${_two(t.minute)}';
}

// --- kurma / düzenleme penceresi -------------------------------------------------------

/// Sohbetteki bir cevabı izlemeye alır. Plan GÖNDERİLMİYOR, yalnız cevabın kimliği:
/// sunucu izlenecek sorguyu kendi kaydından okuyor.
Future<Watch?> showCreateWatchDialog(BuildContext context, AskResult result) {
  return showDialog<Watch>(
    context: context,
    builder: (_) => _WatchFormDialog(result: result),
  );
}

/// Kurulmuş bir izleyicinin eşiğini/sıklığını değiştirir.
/// Soru ve planı değiştirilemez — değer geçmişi ancak aynı ölçüm sürerse anlamlı.
Future<bool?> showWatchThresholdDialog(BuildContext context,
    {required Watch watch}) {
  return showDialog<bool>(
    context: context,
    builder: (_) => _WatchFormDialog(watch: watch),
  );
}

class _WatchFormDialog extends StatefulWidget {
  /// Kurma: izlenecek cevap.
  final AskResult? result;

  /// Düzenleme: var olan izleyici.
  final Watch? watch;

  const _WatchFormDialog({this.result, this.watch});

  @override
  State<_WatchFormDialog> createState() => _WatchFormDialogState();
}

class _WatchFormDialogState extends State<_WatchFormDialog> {
  late final TextEditingController _title;
  late final TextEditingController _threshold;

  late String _kind;
  late String _op;
  late int _interval;

  bool _saving = false;
  String? _error;

  bool get _isEdit => widget.watch != null;

  @override
  void initState() {
    super.initState();
    final watch = widget.watch;

    _title = TextEditingController(
        text: watch?.title ?? _shorten(widget.result?.question ?? ''));
    // Eşik alanı BİNLİK AYRACI OLMADAN dolduruluyor. formatNumber ile doldurulsaydı
    // alanda "1.000" yazardı; kullanıcı ona dokunmadan kaydettiğinde aynı metin geri
    // okunur ve nokta ondalık ayracı sanılırsa eşik sessizce 1'e düşerdi.
    _threshold = TextEditingController(
        text: watch == null ? '' : _plainNumber(watch.threshold));

    _kind = watch?.conditionKind ?? 'value';
    _op = watch?.op ?? 'gt';
    _interval = watch?.intervalMinutes ?? 60;
  }

  @override
  void dispose() {
    _title.dispose();
    _threshold.dispose();
    super.dispose();
  }

  static String _shorten(String text) =>
      text.length <= 60 ? text : '${text.substring(0, 57)}…';

  double? get _thresholdValue => parseUserNumber(_threshold.text);

  Future<void> _save() async {
    final threshold = _thresholdValue;
    if (threshold == null) {
      setState(() => _error = 'Eşik için bir sayı yaz.');
      return;
    }

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      if (_isEdit) {
        await ApiService.updateWatch(
          widget.watch!.id,
          title: _title.text.trim(),
          intervalMinutes: _interval,
          conditionKind: _kind,
          op: _op,
          threshold: threshold,
        );
        if (!mounted) return;
        Navigator.pop(context, true);
      } else {
        final created = await ApiService.createWatch(
          messageId: widget.result!.messageId!,
          intervalMinutes: _interval,
          conditionKind: _kind,
          op: _op,
          threshold: threshold,
          title: _title.text.trim(),
        );
        if (!mounted) return;
        Navigator.pop(context, created);
      }
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = '$e';
        _saving = false;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: Text(_isEdit ? 'Eşiği düzenle' : 'Bu cevabı izle'),
      content: SizedBox(
        width: 460,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                _isEdit
                    ? 'Soru ve ölçüm değişmez; yalnız eşik, sıklık ve ad değişir. '
                        'Başka bir şey izlemek için yeni izleyici kur — böylece bu '
                        'izleyicinin değer geçmişi anlamını korur.'
                    : 'Bu soru bundan sonra belirli aralıklarla kendiliğinden '
                        'çalışacak. Sonuç eşiğin dışına ÇIKTIĞI anda haber verilir; '
                        'dışarıda kaldığı sürece tekrar uyarılmazsın.',
                style: const TextStyle(
                    fontSize: 12.5, color: AppColors.muted, height: 1.45),
              ),
              const SizedBox(height: 18),
              _FieldLabel('İzleyicinin adı'),
              const SizedBox(height: 6),
              TextField(
                controller: _title,
                decoration: const InputDecoration(hintText: 'Örn. Kritik stok'),
              ),
              const SizedBox(height: 16),
              _FieldLabel('Neyi karşılaştıralım'),
              const SizedBox(height: 6),
              SegmentedButton<String>(
                segments: const [
                  ButtonSegment(value: 'value', label: Text('Ölçülen değer')),
                  ButtonSegment(value: 'change', label: Text('Değişim (%)')),
                ],
                selected: {_kind},
                onSelectionChanged: (s) => setState(() => _kind = s.first),
              ),
              const SizedBox(height: 8),
              Text(
                _kind == 'value'
                    ? 'Ölçülen sayının kendisi eşikle karşılaştırılır.'
                    : 'Önceki koşuya göre yüzde değişim karşılaştırılır. '
                        'İlk koşuda karşılaştırma yapılmaz, yalnız taban değer alınır.',
                style: const TextStyle(fontSize: 11.5, color: AppColors.muted),
              ),
              const SizedBox(height: 16),
              _FieldLabel('Koşul'),
              const SizedBox(height: 6),
              Row(
                children: [
                  Expanded(
                    flex: 3,
                    child: DropdownButtonFormField<String>(
                      initialValue: _op,
                      isExpanded: true,
                      items: [
                        for (final e in watchOps.entries)
                          DropdownMenuItem(
                              value: e.key,
                              child: Text(e.value,
                                  overflow: TextOverflow.ellipsis)),
                      ],
                      onChanged: (v) => setState(() => _op = v ?? _op),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    flex: 2,
                    child: TextField(
                      controller: _threshold,
                      keyboardType:
                          const TextInputType.numberWithOptions(decimal: true),
                      decoration: InputDecoration(
                        hintText: _kind == 'change' ? 'örn. 20' : 'örn. 1000',
                        prefixText: _kind == 'change' ? '% ' : null,
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 16),
              _FieldLabel('Ne sıklıkla çalışsın'),
              const SizedBox(height: 6),
              DropdownButtonFormField<int>(
                initialValue: _interval,
                isExpanded: true,
                items: [
                  for (final e in watchIntervals.entries)
                    DropdownMenuItem(value: e.key, child: Text(e.value)),
                ],
                onChanged: (v) => setState(() => _interval = v ?? _interval),
              ),
              if (_error != null) ...[
                const SizedBox(height: 16),
                Text(_error!,
                    style:
                        const TextStyle(color: AppColors.danger, fontSize: 12.5)),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: _saving ? null : () => Navigator.pop(context),
          child: const Text('İptal'),
        ),
        FilledButton(
          onPressed: _saving ? null : _save,
          child: _saving
              ? const ButtonSpinner()
              : Text(_isEdit ? 'Kaydet' : 'İzlemeye al'),
        ),
      ],
    );
  }
}

/// Sayıyı BİNLİK AYRACI OLMADAN, virgüllü ondalıkla yazar: 1500,5.
/// Yalnız düzenleme alanını doldurmak için — okuma yerlerinde [formatNumber] kullanılır.
String _plainNumber(double v) =>
    (v == v.roundToDouble() ? v.toStringAsFixed(0) : '$v').replaceAll('.', ',');

/// Kullanıcının yazdığı eşiği okur ve Türkçe yazımı kabul eder.
///
/// Kural, "1.500" ile "1.5"i ayırmak zorunda: ikisi de nokta içeriyor ama biri bin beş
/// yüz, diğeri bir buçuk. Ayrım noktadan SONRAKİ hane sayısında — üç haneyse binlik
/// ayracıdır. Bu ayrım yapılmasa "1.500" sessizce 1,5 olurdu ve kullanıcı eşiğini bin kat
/// yanlış kurduğunu ancak alarm hiç çalmayınca fark ederdi.
/// Binlik ayracıyla yazılmış bir tam sayı: "1.500", "12.345.678".
/// Baştaki grup sıfırla başlayamaz — "0.125" bu kalıba UYMAZ, ondalıktır.
final RegExp _binlikNoktasi = RegExp(r'^-?[1-9]\d{0,2}(\.\d{3})+$');

double? parseUserNumber(String input) {
  var text = input.trim().replaceAll(' ', '');
  if (text.isEmpty) return null;

  if (text.contains(',')) {
    // Virgül varsa ondalık ayracı odur; nokta ise binlik ayracı olabilir ancak.
    text = text.replaceAll('.', '').replaceAll(',', '.');
  } else if (_binlikNoktasi.hasMatch(text)) {
    // Nokta ancak GERÇEKTEN binlik ayracı gibi duruyorsa siliniyor.
    //
    // Eski kural yalnız "noktadan sonra üç hane var mı" diye bakıyordu ve bu, üç
    // ondalıklı gerçek sayıları ters yönde bozuyordu: "0.125" → "0125" → 125. Kullanıcı
    // hata oranı için 0,125 eşiği kuruyor, sunucuya 125 gidiyor ve alarm hiç çalmıyordu
    // — üstelik belirtisi, yorumda anlatılan "bin kat yanlış eşik ancak alarm hiç
    // çalmayınca fark edilir" cümlesinin birebir kendisi. Aynısı 0.500 ve 1.250 gibi
    // para/oran değerlerinde de oluyordu.
    //
    // Yeni kural binlik yazımın TAMAMINI arıyor: ilk grup 1-3 hane, sonraki her grup tam
    // 3 hane, ve baştaki grup sıfırla başlamıyor. "1.500" ve "12.345.678" binlik sayılır;
    // "0.125", "1.5", "1.50" ondalık kalır.
    text = text.replaceAll('.', '');
  }

  return double.tryParse(text);
}

class _FieldLabel extends StatelessWidget {
  final String text;
  const _FieldLabel(this.text);

  @override
  Widget build(BuildContext context) => Text(
        text.toUpperCase(),
        style: const TextStyle(
            fontSize: 10.5,
            letterSpacing: 0.6,
            fontWeight: FontWeight.w700,
            color: AppColors.muted),
      );
}
