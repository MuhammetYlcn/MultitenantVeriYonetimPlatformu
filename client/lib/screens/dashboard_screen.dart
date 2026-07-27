import 'package:flutter/material.dart';
import '../api_service.dart';
import '../theme/app_theme.dart';
import '../widgets/ui.dart';

// Bir veri setinin özet panosu: KPI kartları + dağılım grafiği + tarih aralığı filtresi.
// Backend'in /aggregate endpoint'ini tüketir (grup özeti + gruplamasız genel toplam).
class DashboardPage extends StatefulWidget {
  final Dataset dataset;

  const DashboardPage({super.key, required this.dataset});

  @override
  State<DashboardPage> createState() => _DashboardPageState();
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

class _DashboardPageState extends State<DashboardPage> {
  late Future<_DashboardData> _future;
  DateTime? _start;
  DateTime? _end;

  @override
  void initState() {
    super.initState();
    _future = _fetch();
  }

  // Gövde blok `{}`: ok gövdeli closure atanan Future'ı döndürür, setState bunu reddeder.
  void _reload() => setState(() {
        _future = _fetch();
      });

  static String _fmtDate(DateTime d) =>
      '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}.${d.year}';

  // Filtre değeri sunucuya ISO biçiminde gider (ekranda gösterim biçimi ayrı).
  static String _isoDate(DateTime d) =>
      '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

  // Büyük sayıları binlik ayraçla yaz: 7100 → 7.100 (grafik ve kartlarda okunurluk).
  static String _fmtNum(double? v) {
    if (v == null) return '—';
    final rounded = v == v.roundToDouble();
    final text = rounded ? v.toStringAsFixed(0) : v.toStringAsFixed(1);
    final parts = text.split('.');
    final intPart = parts.first.replaceAllMapped(
      RegExp(r'(\d)(?=(\d{3})+$)'),
      (m) => '${m[1]}.',
    );
    return parts.length > 1 ? '$intPart,${parts[1]}' : intPart;
  }

  Future<_DashboardData> _fetch() async {
    final id = widget.dataset.id;
    final schema = await ApiService.getSchema(id);

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
    if (dateCol != null && _start != null) {
      filters.add('$dateCol:gte:${_isoDate(_start!)}');
    }
    if (dateCol != null && _end != null) {
      filters.add('$dateCol:lte:${_isoDate(_end!)}');
    }

    // KPI: satır sayısı (filtreye duyarlı, gruplamasız count).
    final countBuckets = await ApiService.aggregate(id, op: 'count', filters: filters);
    final rowCount = countBuckets.isNotEmpty ? countBuckets.first.count : 0;

    // KPI: sayısal metrik varsa genel toplam ve ortalama (gruplamasız).
    double? total, average;
    if (metricCol != null) {
      final sumB =
          await ApiService.aggregate(id, op: 'sum', metric: metricCol, filters: filters);
      total = sumB.isNotEmpty ? sumB.first.value : 0;
      final avgB =
          await ApiService.aggregate(id, op: 'avg', metric: metricCol, filters: filters);
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
    return FutureBuilder<_DashboardData>(
      future: _future,
      builder: (context, snapshot) {
        final d = snapshot.data;
        final filtered = _start != null || _end != null;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            PageHeader(
              title: widget.dataset.name,
              subtitle: d == null
                  ? 'Özet hesaplanıyor…'
                  : filtered
                      ? 'Seçili tarih aralığındaki ${d.rowCount} satırın özeti'
                      : 'Tüm verinin özeti · ${d.rowCount} satır',
              actions: [
                IconButton(
                  onPressed: _reload,
                  icon: const Icon(Icons.refresh),
                  tooltip: 'Yenile',
                ),
              ],
            ),
            Expanded(child: _body(snapshot)),
          ],
        );
      },
    );
  }

  Widget _body(AsyncSnapshot<_DashboardData> snapshot) {
    if (snapshot.connectionState != ConnectionState.done) {
      return const LoadingView(message: 'Özet hesaplanıyor…');
    }
    if (snapshot.hasError) {
      return ErrorView(message: '${snapshot.error}', onRetry: _reload);
    }

    final d = snapshot.data!;
    return ListView(
      padding: const EdgeInsets.only(bottom: 12),
      children: [
        if (d.dateColumn != null) ...[
          _dateFilterBar(d.dateColumn!),
          const SizedBox(height: 16),
        ],
        _kpis(d),
        const SizedBox(height: 16),
        _chartCard(d),
      ],
    );
  }

  // Tarih aralığı filtresi: iki tarih düğmesi + seçim varsa temizleme.
  Widget _dateFilterBar(String dateColumn) {
    final active = _start != null || _end != null;
    return Card(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        child: Wrap(
          spacing: 10,
          runSpacing: 10,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            const IconBadge(
                icon: Icons.date_range_outlined, color: AppColors.brand, size: 32),
            Padding(
              padding: const EdgeInsets.only(right: 4),
              child: Text('$dateColumn aralığı',
                  style: Theme.of(context).textTheme.titleSmall),
            ),
            OutlinedButton.icon(
              onPressed: () => _pickDate(isStart: true),
              icon: const Icon(Icons.play_arrow_outlined, size: 16),
              label: Text(_start == null ? 'Başlangıç' : _fmtDate(_start!)),
            ),
            OutlinedButton.icon(
              onPressed: () => _pickDate(isStart: false),
              icon: const Icon(Icons.stop_outlined, size: 16),
              label: Text(_end == null ? 'Bitiş' : _fmtDate(_end!)),
            ),
            if (active)
              TextButton.icon(
                onPressed: _clearDates,
                icon: const Icon(Icons.close, size: 16),
                label: const Text('Filtreyi kaldır'),
              ),
          ],
        ),
      ),
    );
  }

  // Özet kartları: dar ekranda alt alta, geniş ekranda yan yana akar.
  Widget _kpis(_DashboardData d) {
    final tiles = <Widget>[
      StatTile(
        label: 'Satır',
        value: '${d.rowCount}',
        hint: _start != null || _end != null ? 'seçili aralıkta' : 'tüm veri',
        icon: Icons.table_rows_outlined,
        color: AppColors.brand,
      ),
      if (d.metricColumn != null) ...[
        StatTile(
          label: 'Toplam ${d.metricColumn}',
          value: _fmtNum(d.total),
          hint: 'tüm satırların toplamı',
          icon: Icons.functions,
          color: AppColors.accent,
        ),
        StatTile(
          label: 'Ortalama ${d.metricColumn}',
          value: _fmtNum(d.average),
          hint: 'satır başına',
          icon: Icons.stacked_line_chart,
          color: chartPalette[2],
        ),
      ],
    ];

    return LayoutBuilder(
      builder: (context, c) {
        // 3 karta yer varsa üçlü, yoksa tek sütun (ara genişliklerde ikili).
        final perRow = c.maxWidth >= 760 ? 3 : (c.maxWidth >= 480 ? 2 : 1);
        final width = (c.maxWidth - (perRow - 1) * 14) / perRow;
        return Wrap(
          spacing: 14,
          runSpacing: 14,
          children:
              tiles.map((t) => SizedBox(width: width, child: t)).toList(),
        );
      },
    );
  }

  Widget _chartCard(_DashboardData d) {
    final opLabel =
        d.op == 'sum' ? 'toplam ${d.metricColumn}' : 'satır adedi';
    return SectionCard(
      title: '${d.groupColumn ?? '—'} bazında ${d.op == 'sum' ? 'toplam' : 'adet'}',
      subtitle: 'En yüksek 10 grup · $opLabel',
      trailing: d.chart.isEmpty
          ? null
          : Chip(
              label: Text('${d.chart.length} grup'),
              visualDensity: VisualDensity.compact,
            ),
      child: d.chart.isEmpty
          ? const Padding(
              padding: EdgeInsets.symmetric(vertical: 28),
              child: Center(
                child: Text('Bu aralıkta gösterilecek veri yok.',
                    style: TextStyle(color: AppColors.muted)),
              ),
            )
          : Column(children: _bars(d.chart)),
    );
  }

  // Bağımlılıksız yatay çubuk grafik: her grup için değeriyle orantılı bir çubuk.
  // (Adım 5'te fl_chart'a geçilecek; renkler aynı paletten geleceği için görünüm korunur.)
  List<Widget> _bars(List<AggBucket> data) {
    final maxV = data
        .map((b) => (b.value ?? 0).abs())
        .fold<double>(0, (a, b) => a > b ? a : b);

    return [
      for (var i = 0; i < data.length; i++)
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 7),
          child: Row(
            children: [
              SizedBox(
                width: 96,
                child: Text(
                  data[i].key ?? '—',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodyMedium,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: _Bar(
                  fraction: maxV > 0
                      ? ((data[i].value ?? 0) / maxV).clamp(0.0, 1.0).toDouble()
                      : 0,
                  color: chartPalette[i % chartPalette.length],
                ),
              ),
              const SizedBox(width: 12),
              SizedBox(
                width: 78,
                child: Text(
                  _fmtNum(data[i].value),
                  textAlign: TextAlign.right,
                  style: const TextStyle(
                      fontWeight: FontWeight.w600, fontSize: 13.5),
                ),
              ),
            ],
          ),
        ),
    ];
  }
}

// Tek çubuk: sönük bir yol + üzerinde değere orantılı, degrade dolgulu bölüm.
class _Bar extends StatelessWidget {
  final double fraction;
  final Color color;

  const _Bar({required this.fraction, required this.color});

  @override
  Widget build(BuildContext context) {
    return Stack(
      children: [
        Container(
          height: 26,
          decoration: BoxDecoration(
            color: AppColors.surfaceAlt,
            borderRadius: BorderRadius.circular(8),
          ),
        ),
        // Değer değişince çubuk yumuşakça uzar/kısalır (filtre uygulandığında görünür).
        FractionallySizedBox(
          widthFactor: fraction,
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 350),
            curve: Curves.easeOutCubic,
            height: 26,
            decoration: BoxDecoration(
              gradient: LinearGradient(
                colors: [color.withValues(alpha: 0.75), color],
              ),
              borderRadius: BorderRadius.circular(8),
            ),
          ),
        ),
      ],
    );
  }
}
