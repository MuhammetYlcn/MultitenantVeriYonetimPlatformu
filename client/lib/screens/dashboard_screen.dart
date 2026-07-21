import 'package:flutter/material.dart';
import '../api_service.dart';

// Bir veri setinin özet panosu: KPI kartları + grafik + tarih aralığı filtresi.
// Backend'in /aggregate endpoint'ini tüketir (grup özeti + gruplamasız genel toplam).
class DashboardScreen extends StatefulWidget {
  final String datasetId;
  final String datasetName;

  const DashboardScreen({
    super.key,
    required this.datasetId,
    required this.datasetName,
  });

  @override
  State<DashboardScreen> createState() => _DashboardScreenState();
}

// Panonun ihtiyaç duyduğu her şeyi tek seferde toplayan sonuç nesnesi.
class _DashboardData {
  final String? groupColumn;
  final String? metricColumn;
  final String? dateColumn;
  final String op;
  final int rowCount;
  final double? total;
  final double? average;
  final List<AggBucket> chart;

  _DashboardData({
    required this.groupColumn,
    required this.metricColumn,
    required this.dateColumn,
    required this.op,
    required this.rowCount,
    required this.total,
    required this.average,
    required this.chart,
  });
}

class _DashboardScreenState extends State<DashboardScreen> {
  late Future<_DashboardData> _future;
  DateTime? _start;
  DateTime? _end;

  @override
  void initState() {
    super.initState();
    _future = _fetch();
  }

  void _reload() => setState(() => _future = _fetch());

  static String _fmtDate(DateTime d) =>
      '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

  static String _fmtNum(double? v) {
    if (v == null) return '—';
    return v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);
  }

  Future<_DashboardData> _fetch() async {
    final schema = await ApiService.getSchema(widget.datasetId);
    final id = widget.datasetId;

    // Kolonları tiplerine göre ayır; grafik/metrik için makul varsayılanlar seç.
    final textCols = schema.where((c) => c.type == 'text').toList();
    final numCols = schema.where((c) => c.type == 'number').toList();
    final dateCols = schema.where((c) => c.type == 'date').toList();

    final groupCol = textCols.isNotEmpty
        ? textCols.first.name
        : (schema.isNotEmpty ? schema.first.name : null);
    final metricCol = numCols.isNotEmpty ? numCols.first.name : null;
    final dateCol = dateCols.isNotEmpty ? dateCols.first.name : null;
    final op = metricCol != null ? 'sum' : 'count';

    // Seçili tarih aralığı → filtre koşulları (tarih kolonu varsa).
    final filters = <String>[];
    if (dateCol != null && _start != null) filters.add('$dateCol:gte:${_fmtDate(_start!)}');
    if (dateCol != null && _end != null) filters.add('$dateCol:lte:${_fmtDate(_end!)}');

    // KPI: satır sayısı (filtreye duyarlı, gruplamasız count).
    final countBuckets = await ApiService.aggregate(id, op: 'count', filters: filters);
    final rowCount = countBuckets.isNotEmpty ? countBuckets.first.count : 0;

    // KPI: sayısal metrik varsa genel toplam ve ortalama (gruplamasız).
    double? total, average;
    if (metricCol != null) {
      final sumB = await ApiService.aggregate(id, op: 'sum', metric: metricCol, filters: filters);
      total = sumB.isNotEmpty ? sumB.first.value : 0;
      final avgB = await ApiService.aggregate(id, op: 'avg', metric: metricCol, filters: filters);
      average = avgB.isNotEmpty ? avgB.first.value : 0;
    }

    // Grafik: grup bazında (en yüksek 10, değere göre azalan).
    final chart = groupCol != null
        ? await ApiService.aggregate(id,
            groupBy: groupCol,
            op: op,
            metric: metricCol,
            sort: 'value',
            dir: 'desc',
            limit: 10,
            filters: filters)
        : <AggBucket>[];

    return _DashboardData(
      groupColumn: groupCol,
      metricColumn: metricCol,
      dateColumn: dateCol,
      op: op,
      rowCount: rowCount,
      total: total,
      average: average,
      chart: chart,
    );
  }

  Future<void> _pickDate({required bool isStart}) async {
    final picked = await showDatePicker(
      context: context,
      initialDate: (isStart ? _start : _end) ?? DateTime(2026, 1, 1),
      firstDate: DateTime(2000),
      lastDate: DateTime(2100),
    );
    if (picked == null) return;
    setState(() {
      if (isStart) {
        _start = picked;
      } else {
        _end = picked;
      }
    });
    _reload();
  }

  void _clearDates() {
    setState(() {
      _start = null;
      _end = null;
    });
    _reload();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text(widget.datasetName),
        actions: [
          IconButton(onPressed: _reload, icon: const Icon(Icons.refresh), tooltip: 'Yenile'),
        ],
      ),
      body: FutureBuilder<_DashboardData>(
        future: _future,
        builder: (context, snapshot) {
          if (snapshot.connectionState != ConnectionState.done) {
            return const Center(child: CircularProgressIndicator());
          }
          if (snapshot.hasError) {
            return Center(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Text('Yüklenemedi: ${snapshot.error}',
                    style: const TextStyle(color: Colors.red), textAlign: TextAlign.center),
              ),
            );
          }

          final d = snapshot.data!;
          return ListView(
            padding: const EdgeInsets.all(16),
            children: [
              if (d.dateColumn != null) _dateFilterBar(d.dateColumn!),
              _kpiRow(d),
              const SizedBox(height: 8),
              _chartCard(d),
            ],
          );
        },
      ),
    );
  }

  Widget _dateFilterBar(String dateColumn) {
    final active = _start != null || _end != null;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Row(
          children: [
            const Icon(Icons.date_range, size: 20),
            const SizedBox(width: 8),
            Expanded(
              child: Wrap(
                spacing: 8,
                runSpacing: 8,
                crossAxisAlignment: WrapCrossAlignment.center,
                children: [
                  OutlinedButton(
                    onPressed: () => _pickDate(isStart: true),
                    child: Text(_start == null ? 'Başlangıç' : _fmtDate(_start!)),
                  ),
                  OutlinedButton(
                    onPressed: () => _pickDate(isStart: false),
                    child: Text(_end == null ? 'Bitiş' : _fmtDate(_end!)),
                  ),
                  if (active)
                    TextButton.icon(
                      onPressed: _clearDates,
                      icon: const Icon(Icons.clear, size: 18),
                      label: const Text('Temizle'),
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _kpiRow(_DashboardData d) {
    final tiles = <Widget>[
      _kpi('Satır', '${d.rowCount}'),
      if (d.metricColumn != null) ...[
        _kpi('Toplam ${d.metricColumn}', _fmtNum(d.total)),
        _kpi('Ortalama ${d.metricColumn}', _fmtNum(d.average)),
      ],
    ];
    return Wrap(spacing: 12, runSpacing: 12, children: tiles);
  }

  Widget _kpi(String label, String value) {
    return SizedBox(
      width: 150,
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(label,
                  style: TextStyle(color: Colors.grey[600], fontSize: 13),
                  maxLines: 1, overflow: TextOverflow.ellipsis),
              const SizedBox(height: 6),
              Text(value, style: const TextStyle(fontSize: 24, fontWeight: FontWeight.bold)),
            ],
          ),
        ),
      ),
    );
  }

  Widget _chartCard(_DashboardData d) {
    final opLabel = d.op == 'sum' ? 'Toplam ${d.metricColumn}' : 'Adet';
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('${d.groupColumn ?? '—'} bazında $opLabel',
                style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
            const SizedBox(height: 16),
            if (d.chart.isEmpty)
              const Padding(
                padding: EdgeInsets.symmetric(vertical: 24),
                child: Center(child: Text('Gösterilecek veri yok.')),
              )
            else
              ..._bars(d.chart),
          ],
        ),
      ),
    );
  }

  // Bağımlılıksız yatay çubuk grafik: her grup için değeriyle orantılı bir çubuk.
  List<Widget> _bars(List<AggBucket> data) {
    final maxV = data
        .map((b) => (b.value ?? 0).abs())
        .fold<double>(0, (a, b) => a > b ? a : b);

    return data.map((b) {
      final frac = maxV > 0 ? ((b.value ?? 0) / maxV).clamp(0.0, 1.0) : 0.0;
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: 6),
        child: Row(
          children: [
            SizedBox(
              width: 90,
              child: Text(b.key ?? '—', maxLines: 1, overflow: TextOverflow.ellipsis),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Stack(
                children: [
                  Container(
                    height: 24,
                    decoration: BoxDecoration(
                      color: Colors.indigo.withValues(alpha: 0.10),
                      borderRadius: BorderRadius.circular(4),
                    ),
                  ),
                  FractionallySizedBox(
                    widthFactor: frac,
                    child: Container(
                      height: 24,
                      decoration: BoxDecoration(
                        color: Colors.indigo,
                        borderRadius: BorderRadius.circular(4),
                      ),
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(width: 8),
            SizedBox(
              width: 64,
              child: Text(_fmtNum(b.value), textAlign: TextAlign.right),
            ),
          ],
        ),
      );
    }).toList();
  }
}
