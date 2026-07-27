import 'package:flutter/material.dart';
import '../api_service.dart';
import '../theme/app_theme.dart';
import '../widgets/ui.dart';

// Bir veri setinin ham satırları. Kolonlar SABİT değil — şemadan üretilir; böylece
// hangi CSV/Excel yüklendiyse tablo kendini ona göre kurar (dinamik render).
class DataTablePage extends StatefulWidget {
  final Dataset dataset;
  final VoidCallback onBack;
  final VoidCallback onOpenDashboard;

  const DataTablePage({
    super.key,
    required this.dataset,
    required this.onBack,
    required this.onOpenDashboard,
  });

  @override
  State<DataTablePage> createState() => _DataTablePageState();
}

class _DataTablePageState extends State<DataTablePage> {
  late Future<_TableData> _future;
  // "Satır ekle" formunu şemaya göre kurmak için son yüklenen kolonları saklarız.
  List<SchemaColumn> _schema = [];

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  // Şema (kolonlar) + ilk sayfa satırlar birlikte çekilir.
  Future<_TableData> _load() async {
    final schema = await ApiService.getSchema(widget.dataset.id);
    final page = await ApiService.getRows(widget.dataset.id);
    _schema = schema;
    return _TableData(schema, page);
  }

  // Gövde blok `{}`: ok gövdeli closure atanan Future'ı döndürür, setState bunu reddeder.
  void _reload() => setState(() {
        _future = _load();
      });

  // Şemaya göre dinamik form: her kolon için bir alan. Kaydedince tek satır eklenir.
  Future<void> _addRow() async {
    if (_schema.isEmpty) {
      showSnack(context, 'Bu veri setinin şeması yok; önce bir dosya yükle.',
          isError: true);
      return;
    }
    final controllers = {for (final c in _schema) c.name: TextEditingController()};

    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Satır ekle'),
        content: SizedBox(
          width: 380,
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: _schema
                  .map((c) => Padding(
                        padding: const EdgeInsets.only(bottom: 14),
                        child: TextField(
                          controller: controllers[c.name],
                          keyboardType: c.type == 'number'
                              ? const TextInputType.numberWithOptions(decimal: true)
                              : TextInputType.text,
                          decoration: InputDecoration(
                            labelText: c.name,
                            helperText: _typeHint(c.type),
                            prefixIcon: Icon(_typeIcon(c.type), size: 18),
                          ),
                        ),
                      ))
                  .toList(),
            ),
          ),
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx, false),
              child: const Text('Vazgeç')),
          FilledButton(
              onPressed: () => Navigator.pop(ctx, true),
              child: const Text('Kaydet')),
        ],
      ),
    );

    // Değerleri controller'lar kapanmadan ÖNCE oku.
    final values = {
      for (final e in controllers.entries) e.key: e.value.text.trim(),
    };
    for (final c in controllers.values) {
      c.dispose();
    }
    if (ok != true) return;

    try {
      await ApiService.addRow(widget.dataset.id, values);
      if (!mounted) return;
      showSnack(context, 'Satır eklendi.');
      _reload();
    } catch (e) {
      if (!mounted) return;
      showSnack(context, 'Eklenemedi: $e', isError: true);
    }
  }

  // Alanın altında gösterilecek tip ipucu (kullanıcı doğru biçimde girsin).
  static String _typeHint(String type) => switch (type) {
        'number' => 'sayı (örn. 1500.50)',
        'date' => 'tarih (YYYY-AA-GG)',
        _ => 'metin',
      };

  static IconData _typeIcon(String type) => switch (type) {
        'number' => Icons.tag,
        'date' => Icons.event_outlined,
        _ => Icons.text_fields,
      };

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<_TableData>(
      future: _future,
      builder: (context, snap) {
        final data = snap.data;
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            PageHeader(
              leading: IconButton(
                onPressed: widget.onBack,
                icon: const Icon(Icons.arrow_back),
                tooltip: 'Veri setlerine dön',
              ),
              title: widget.dataset.name,
              subtitle: data == null
                  ? 'Yükleniyor…'
                  : '${data.page.total} satır · ${data.schema.length} kolon'
                      '${data.page.total > data.page.rows.length ? " · ilk ${data.page.rows.length} tanesi gösteriliyor" : ""}',
              actions: [
                IconButton(
                  onPressed: _reload,
                  icon: const Icon(Icons.refresh),
                  tooltip: 'Yenile',
                ),
                OutlinedButton.icon(
                  onPressed: widget.onOpenDashboard,
                  icon: const Icon(Icons.insights_outlined, size: 18),
                  label: const Text('Panele bak'),
                ),
                // Viewer yalnız okur → satır ekleme butonu hiç gösterilmez. Bu sadece
                // arayüz sadeliği; asıl koruma backend'de ([Authorize] → 403).
                if (ApiService.canWrite)
                  FilledButton.icon(
                    onPressed: _addRow,
                    icon: const Icon(Icons.add, size: 18),
                    label: const Text('Satır ekle'),
                  ),
              ],
            ),
            Expanded(child: _table(snap)),
          ],
        );
      },
    );
  }

  Widget _table(AsyncSnapshot<_TableData> snap) {
    if (snap.connectionState != ConnectionState.done) {
      return const LoadingView(message: 'Satırlar getiriliyor…');
    }
    if (snap.hasError) {
      return ErrorView(message: '${snap.error}', onRetry: _reload);
    }

    final cols = snap.data!.schema;
    final rows = snap.data!.page.rows;

    if (cols.isEmpty) {
      return const EmptyState(
        icon: Icons.view_column_outlined,
        title: 'Bu veri setinin şeması yok',
        message: 'Kolonlar bir CSV/Excel dosyası yüklendiğinde otomatik oluşur.',
      );
    }
    if (rows.isEmpty) {
      return EmptyState(
        icon: Icons.inbox_outlined,
        title: 'Henüz satır yok',
        message: '${cols.length} kolon tanımlı ama içinde veri yok.',
        action: ApiService.canWrite
            ? FilledButton.icon(
                onPressed: _addRow,
                icon: const Icon(Icons.add, size: 18),
                label: const Text('İlk satırı ekle'),
              )
            : null,
      );
    }

    return Card(
      child: Scrollbar(
        child: SingleChildScrollView(
          child: Scrollbar(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: DataTable(
                // Kolonlar şemadan: adı başlık, tip simgesiyle; sayısal tipler sağa yaslı.
                columns: cols
                    .map((c) => DataColumn(
                          numeric: c.type == 'number',
                          label: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              Icon(_typeIcon(c.type),
                                  size: 13, color: AppColors.muted),
                              const SizedBox(width: 6),
                              Text(c.name.toUpperCase()),
                            ],
                          ),
                        ))
                    .toList(),
                // Her satır için, kolon sırasına göre değerleri diz. Çift satırlar hafif
                // dolgulu: uzun tablolarda göz satırı kaybetmesin.
                rows: [
                  for (var i = 0; i < rows.length; i++)
                    DataRow(
                      color: WidgetStatePropertyAll(i.isEven
                          ? Colors.transparent
                          : AppColors.surfaceAlt.withValues(alpha: 0.45)),
                      cells: cols
                          .map((c) => DataCell(_cell(rows[i].data[c.name])))
                          .toList(),
                    ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  // Boş değerler sönük bir tire ile gösterilir — hücre boş kalıp tablo delik delik durmasın.
  Widget _cell(dynamic value) {
    if (value == null) {
      return const Text('—', style: TextStyle(color: AppColors.muted));
    }
    return Text(_fmt(value));
  }

  // JSONB değerini okunur metne çevir: ISO tarih ise yalnız gün kısmı.
  static String _fmt(dynamic v) {
    final s = v.toString();
    if (s.length >= 10 && RegExp(r'^\d{4}-\d{2}-\d{2}T').hasMatch(s)) {
      return s.substring(0, 10);
    }
    return s;
  }
}

// FutureBuilder'a tek tip veri taşımak için küçük demet: şema + satır sayfası.
class _TableData {
  final List<SchemaColumn> schema;
  final RowPage page;

  _TableData(this.schema, this.page);
}
