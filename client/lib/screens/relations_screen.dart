import 'package:flutter/material.dart';

import '../api_service.dart';
import '../theme/app_theme.dart';
import '../widgets/ui.dart';

// Veri setleri arasındaki bağlar.
//
// Bu ekran BİRİNCİL iş akışı değil, DÜZELTME aracıdır: bağları normalde sistem dosya
// yüklendiğinde kendisi bulur (bkz. RelationDetector). Kullanıcının burada işi olması
// yalnızca iki durumda gerekir — sistem yanlış bir bağ bulduğunda silmek, ya da
// bulamadığı bir bağı elle eklemek.
//
// Bu yüzden ekran "boş form" olarak değil, "bulunanların listesi" olarak açılıyor.
class RelationsPage extends StatefulWidget {
  const RelationsPage({super.key});

  @override
  State<RelationsPage> createState() => _RelationsPageState();
}

class _RelationsPageState extends State<RelationsPage> {
  late Future<List<DatasetRelation>> _future;

  @override
  void initState() {
    super.initState();
    _future = ApiService.relations();
  }

  // Future'ı setState'in İÇİNDE kurma: içerideki istisna setState'i patlatır
  // (bkz. daha önce aynı tuzağa düşülen ekranlar).
  void _reload() {
    final refreshed = ApiService.relations();
    setState(() => _future = refreshed);
  }

  Future<void> _add() async {
    final created = await showDialog<bool>(
      context: context,
      builder: (_) => const _AddRelationDialog(),
    );
    if (created == true && mounted) _reload();
  }

  Future<void> _delete(DatasetRelation relation) async {
    try {
      await ApiService.deleteRelation(relation.id);
      if (!mounted) return;
      _reload();
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
        PageHeader(
          leading: const IconBadge(icon: Icons.link, color: AppColors.accent),
          title: 'İlişkiler',
          subtitle: 'Bağlı veri setleri tek soruda birlikte sorgulanabilir. '
              'Bağlar dosya yüklenince kendiliğinden bulunur.',
          actions: [
            if (ApiService.canWrite)
              FilledButton.icon(
                onPressed: _add,
                icon: const Icon(Icons.add, size: 18),
                label: const Text('İlişki ekle'),
              ),
          ],
        ),
        Expanded(
          child: FutureBuilder<List<DatasetRelation>>(
            future: _future,
            builder: (context, snapshot) {
              if (snapshot.connectionState != ConnectionState.done) {
                return const LoadingView();
              }
              if (snapshot.hasError) {
                return ErrorView(message: '${snapshot.error}', onRetry: _reload);
              }

              final relations = snapshot.data ?? [];
              if (relations.isEmpty) {
                return const EmptyState(
                  icon: Icons.link_off,
                  title: 'Henüz bağ yok',
                  message:
                      'İki veri setinde ortak bir alan varsa (örneğin müşteri numarası), '
                      'dosya yüklendiğinde sistem bunu kendisi bulur. '
                      'Bulunamadıysa buradan elle ekleyebilirsin.',
                );
              }

              return ListView.separated(
                itemCount: relations.length,
                separatorBuilder: (_, _) => const SizedBox(height: 10),
                itemBuilder: (_, i) => _RelationCard(
                  relation: relations[i],
                  onDelete:
                      ApiService.canWrite ? () => _delete(relations[i]) : null,
                ),
              );
            },
          ),
        ),
      ],
    );
  }
}

class _RelationCard extends StatelessWidget {
  final DatasetRelation relation;
  final VoidCallback? onDelete;

  const _RelationCard({required this.relation, required this.onDelete});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 16),
      decoration: BoxDecoration(
        color: AppColors.surface,
        border: Border.all(color: AppColors.border),
        borderRadius: BorderRadius.circular(AppRadius.card),
      ),
      child: Row(
        children: [
          Expanded(
            child: Wrap(
              crossAxisAlignment: WrapCrossAlignment.center,
              spacing: 10,
              runSpacing: 8,
              children: [
                _Side(dataset: relation.fromDatasetName, column: relation.fromColumn),
                const Icon(Icons.sync_alt, size: 16, color: AppColors.accent),
                _Side(dataset: relation.toDatasetName, column: relation.toColumn),
              ],
            ),
          ),
          const SizedBox(width: 12),
          // Kaynağı göster: makine işi olan bir bağ yanlış olabilir, kullanıcı
          // hangisine güveneceğini bilmeli.
          _Origin(isAuto: relation.isAutoDetected),
          if (onDelete != null)
            IconButton(
              onPressed: onDelete,
              icon: const Icon(Icons.delete_outline, size: 19),
              tooltip: 'Bağı kaldır',
            ),
        ],
      ),
    );
  }
}

class _Side extends StatelessWidget {
  final String dataset;
  final String column;

  const _Side({required this.dataset, required this.column});

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        const Icon(Icons.table_chart_outlined, size: 15, color: AppColors.muted),
        const SizedBox(width: 7),
        Text(dataset,
            style: const TextStyle(fontSize: 13.5, fontWeight: FontWeight.w600)),
        const Text(' · ', style: TextStyle(color: AppColors.muted)),
        Text(column,
            style: const TextStyle(
                fontSize: 13, color: AppColors.accent, fontFamily: 'monospace')),
      ],
    );
  }
}

class _Origin extends StatelessWidget {
  final bool isAuto;
  const _Origin({required this.isAuto});

  @override
  Widget build(BuildContext context) {
    final color = isAuto ? AppColors.muted : AppColors.brand;
    return Tooltip(
      message: isAuto
          ? 'Veriler karşılaştırılarak bulundu. Yanlışsa kaldırabilirsin.'
          : 'Elle tanımlandı.',
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.12),
          border: Border.all(color: color.withValues(alpha: 0.3)),
          borderRadius: BorderRadius.circular(999),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(isAuto ? Icons.auto_awesome : Icons.person_outline,
                size: 12, color: color),
            const SizedBox(width: 5),
            Text(isAuto ? 'otomatik' : 'elle',
                style: TextStyle(
                    fontSize: 11.5, color: color, fontWeight: FontWeight.w600)),
          ],
        ),
      ),
    );
  }
}

// --- ekleme penceresi ---------------------------------------------------------------

/// Dört seçim: iki veri seti, iki kolon. Kolon listeleri seçilen setin şemasından
/// gelir — uydurma kolon adı yazılamaz.
class _AddRelationDialog extends StatefulWidget {
  const _AddRelationDialog();

  @override
  State<_AddRelationDialog> createState() => _AddRelationDialogState();
}

class _AddRelationDialogState extends State<_AddRelationDialog> {
  List<Dataset> _datasets = [];
  bool _loading = true;
  bool _saving = false;
  String? _error;

  Dataset? _from;
  Dataset? _to;
  String? _fromColumn;
  String? _toColumn;

  List<SchemaColumn> _fromColumns = [];
  List<SchemaColumn> _toColumns = [];

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final datasets = await ApiService.getDatasets();
      if (!mounted) return;
      setState(() {
        _datasets = datasets;
        _loading = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.toString();
        _loading = false;
      });
    }
  }

  Future<void> _pickFrom(Dataset? dataset) async {
    setState(() {
      _from = dataset;
      _fromColumn = null;
      _fromColumns = [];
    });
    if (dataset == null) return;

    final columns = await ApiService.getSchema(dataset.id);
    if (!mounted) return;
    setState(() => _fromColumns = columns);
  }

  Future<void> _pickTo(Dataset? dataset) async {
    setState(() {
      _to = dataset;
      _toColumn = null;
      _toColumns = [];
    });
    if (dataset == null) return;

    final columns = await ApiService.getSchema(dataset.id);
    if (!mounted) return;
    setState(() => _toColumns = columns);
  }

  bool get _ready =>
      _from != null && _to != null && _fromColumn != null && _toColumn != null;

  Future<void> _save() async {
    if (!_ready || _saving) return;
    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      await ApiService.createRelation(
        fromDatasetId: _from!.id,
        fromColumn: _fromColumn!,
        toDatasetId: _to!.id,
        toColumn: _toColumn!,
      );
      if (!mounted) return;
      Navigator.pop(context, true);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.toString();
        _saving = false;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('İlişki ekle'),
      content: SizedBox(
        width: 460,
        child: _loading
            ? const Padding(
                padding: EdgeInsets.all(30),
                child: Center(child: CircularProgressIndicator()))
            : Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Text(
                    'İki veri setinde aynı şeyi gösteren alanları eşleştir. '
                    'Örneğin satışlardaki müşteri numarası ile müşteri kartındaki numara.',
                    style: TextStyle(fontSize: 12.5, color: AppColors.muted, height: 1.45),
                  ),
                  const SizedBox(height: 20),
                  _Picker(
                    label: 'Bu veri setindeki',
                    datasets: _datasets,
                    selectedDataset: _from,
                    onDataset: _pickFrom,
                    columns: _fromColumns,
                    selectedColumn: _fromColumn,
                    onColumn: (c) => setState(() => _fromColumn = c),
                  ),
                  const SizedBox(height: 8),
                  const Center(
                    child: Icon(Icons.sync_alt, size: 18, color: AppColors.accent),
                  ),
                  const SizedBox(height: 8),
                  _Picker(
                    label: 'şu veri setindeki alana eşit',
                    // Aynı seti iki kez seçmek anlamsız; listeden çıkarılıyor.
                    datasets: _datasets.where((d) => d.id != _from?.id).toList(),
                    selectedDataset: _to,
                    onDataset: _pickTo,
                    columns: _toColumns,
                    selectedColumn: _toColumn,
                    onColumn: (c) => setState(() => _toColumn = c),
                  ),
                  if (_error != null) ...[
                    const SizedBox(height: 16),
                    Text(_error!,
                        style: const TextStyle(color: AppColors.danger, fontSize: 12.5)),
                  ],
                ],
              ),
      ),
      actions: [
        TextButton(
          onPressed: _saving ? null : () => Navigator.pop(context),
          child: const Text('İptal'),
        ),
        FilledButton(
          onPressed: _ready && !_saving ? _save : null,
          child: _saving ? const ButtonSpinner() : const Text('Kaydet'),
        ),
      ],
    );
  }
}

class _Picker extends StatelessWidget {
  final String label;
  final List<Dataset> datasets;
  final Dataset? selectedDataset;
  final ValueChanged<Dataset?> onDataset;
  final List<SchemaColumn> columns;
  final String? selectedColumn;
  final ValueChanged<String?> onColumn;

  const _Picker({
    required this.label,
    required this.datasets,
    required this.selectedDataset,
    required this.onDataset,
    required this.columns,
    required this.selectedColumn,
    required this.onColumn,
  });

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label.toUpperCase(),
            style: const TextStyle(
                fontSize: 10.5,
                letterSpacing: 0.6,
                fontWeight: FontWeight.w700,
                color: AppColors.muted)),
        const SizedBox(height: 8),
        Row(
          children: [
            Expanded(
              child: DropdownButtonFormField<Dataset>(
                initialValue: selectedDataset,
                isExpanded: true,
                decoration: const InputDecoration(
                  hintText: 'Veri seti',
                  contentPadding: EdgeInsets.symmetric(horizontal: 14, vertical: 12),
                ),
                items: [
                  for (final d in datasets)
                    DropdownMenuItem(
                      value: d,
                      child: Text(d.name, overflow: TextOverflow.ellipsis),
                    ),
                ],
                onChanged: onDataset,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: DropdownButtonFormField<String>(
                initialValue: selectedColumn,
                isExpanded: true,
                decoration: const InputDecoration(
                  hintText: 'Kolon',
                  contentPadding: EdgeInsets.symmetric(horizontal: 14, vertical: 12),
                ),
                items: [
                  for (final c in columns)
                    DropdownMenuItem(
                      value: c.name,
                      child: Text(c.name, overflow: TextOverflow.ellipsis),
                    ),
                ],
                // Veri seti seçilmeden kolon seçilemez.
                onChanged: columns.isEmpty ? null : onColumn,
              ),
            ),
          ],
        ),
      ],
    );
  }
}
