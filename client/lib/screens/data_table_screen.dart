import 'package:flutter/material.dart';
import '../api_service.dart';
import 'dashboard_screen.dart';

// Bir veri setinin ham satırlarını gösterir. Kolonlar SABİT değil — şemadan üretilir;
// böylece hangi CSV/Excel yüklendiyse tablo kendini ona göre kurar (dinamik render).
class DataTableScreen extends StatefulWidget {
  final String datasetId;
  final String datasetName;

  const DataTableScreen({
    super.key,
    required this.datasetId,
    required this.datasetName,
  });

  @override
  State<DataTableScreen> createState() => _DataTableScreenState();
}

class _DataTableScreenState extends State<DataTableScreen> {
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
    final schema = await ApiService.getSchema(widget.datasetId);
    final page = await ApiService.getRows(widget.datasetId);
    _schema = schema;
    return _TableData(schema, page);
  }

  // Şemaya göre dinamik form: her kolon için bir alan. Kaydedince tek satır eklenir.
  Future<void> _addRow() async {
    if (_schema.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Bu veri setinin şeması yok; önce dosya yükle.')));
      return;
    }
    final controllers = {for (final c in _schema) c.name: TextEditingController()};

    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Satır ekle'),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: _schema
                .map((c) => Padding(
                      padding: const EdgeInsets.symmetric(vertical: 6),
                      child: TextField(
                        controller: controllers[c.name],
                        keyboardType: c.type == 'number'
                            ? const TextInputType.numberWithOptions(decimal: true)
                            : TextInputType.text,
                        decoration: InputDecoration(
                          labelText: c.name,
                          helperText: _typeHint(c.type),
                          border: const OutlineInputBorder(),
                        ),
                      ),
                    ))
                .toList(),
          ),
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx, false), child: const Text('İptal')),
          FilledButton(
              onPressed: () => Navigator.pop(ctx, true), child: const Text('Kaydet')),
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
      await ApiService.addRow(widget.datasetId, values);
      if (!mounted) return;
      ScaffoldMessenger.of(context)
          .showSnackBar(const SnackBar(content: Text('Satır eklendi.')));
      setState(() => _future = _load());
    } catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text('Eklenemedi: $e')));
    }
  }

  // Alanın altında gösterilecek tip ipucu (kullanıcı doğru biçimde girsin).
  static String _typeHint(String type) => switch (type) {
        'number' => 'sayı (örn. 1500.50)',
        'date' => 'tarih (YYYY-MM-DD)',
        _ => 'metin',
      };

  void _openDashboard() {
    Navigator.push(
      context,
      MaterialPageRoute(
        builder: (_) => DashboardScreen(
          datasetId: widget.datasetId,
          datasetName: widget.datasetName,
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text(widget.datasetName),
        actions: [
          IconButton(
            onPressed: _openDashboard,
            icon: const Icon(Icons.bar_chart),
            tooltip: 'Panel',
          ),
          IconButton(
            onPressed: () => setState(() => _future = _load()),
            icon: const Icon(Icons.refresh),
            tooltip: 'Yenile',
          ),
        ],
      ),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: _addRow,
        icon: const Icon(Icons.add),
        label: const Text('Satır ekle'),
      ),
      body: FutureBuilder<_TableData>(
        future: _future,
        builder: (context, snap) {
          if (snap.connectionState != ConnectionState.done) {
            return const Center(child: CircularProgressIndicator());
          }
          if (snap.hasError) {
            return Center(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Text('Yüklenemedi: ${snap.error}',
                    style: const TextStyle(color: Colors.red),
                    textAlign: TextAlign.center),
              ),
            );
          }

          final cols = snap.data!.schema;
          final rows = snap.data!.page.rows;
          if (cols.isEmpty) {
            return const Center(
                child: Text('Bu veri setinin şeması yok.\nÖnce bir dosya yükle.',
                    textAlign: TextAlign.center));
          }

          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
                child: Text(
                  '${snap.data!.page.total} satır • ${cols.length} kolon'
                  '${snap.data!.page.total > rows.length ? " (ilk ${rows.length} gösteriliyor)" : ""}',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
              Expanded(
                child: SingleChildScrollView(
                  scrollDirection: Axis.vertical,
                  child: SingleChildScrollView(
                    scrollDirection: Axis.horizontal,
                    child: DataTable(
                      // Kolonlar şemadan: adı başlık, sayısal tipler sağa yaslı.
                      columns: cols
                          .map((c) => DataColumn(
                                label: Text(c.name),
                                numeric: c.type == 'number',
                              ))
                          .toList(),
                      // Her satır için, kolon sırasına göre değerleri diz.
                      rows: rows
                          .map((r) => DataRow(
                                cells: cols
                                    .map((c) =>
                                        DataCell(Text(_fmt(r.data[c.name]))))
                                    .toList(),
                              ))
                          .toList(),
                    ),
                  ),
                ),
              ),
            ],
          );
        },
      ),
    );
  }

  // JSONB değerini okunur metne çevir: null → '—', ISO tarih ise yalnız gün kısmı.
  static String _fmt(dynamic v) {
    if (v == null) return '—';
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
