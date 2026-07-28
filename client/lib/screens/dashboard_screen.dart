import 'package:flutter/material.dart';

import '../api_service.dart';
import '../theme/app_theme.dart';
import '../widgets/charts.dart';
import '../widgets/ui.dart';

// Bir veri setinin özet panosu: KPI kartları + dağılım grafiği + zaman serisi.
// Backend'in /aggregate ucunu tüketir (gruplu ve gruplamasız).
//
// Adım 5'te pano "sabit bir görünüm" olmaktan çıktı: kullanıcı hangi kolona göre
// gruplanacağını, hangi sayısal kolonun hangi işlemle (toplam/ortalama/…) özetleneceğini
// ve grafiğin türünü kendisi seçer. Her seçim yeni bir /aggregate isteğine dönüşür —
// hesap sunucuda kalır, istemci yalnız çizer.

/// Kullanıcıya sunulan özetleme işlemleri. Anahtar backend'in beklediği `op` değeri.
const _opLabels = <String, String>{
  'sum': 'Toplam',
  'avg': 'Ortalama',
  'count': 'Adet',
  'min': 'En düşük',
  'max': 'En yüksek',
};

/// Zaman serisinde tarihlerin hangi aralıkla toplanacağı (backend `bucket` değeri).
const _bucketLabels = <String, String>{
  'day': 'Gün',
  'week': 'Hafta',
  'month': 'Ay',
  'year': 'Yıl',
};

const _monthsShort = [
  'Oca', 'Şub', 'Mar', 'Nis', 'May', 'Haz', //
  'Tem', 'Ağu', 'Eyl', 'Eki', 'Kas', 'Ara',
];

class DashboardPage extends StatefulWidget {
  final Dataset dataset;

  const DashboardPage({super.key, required this.dataset});

  @override
  State<DashboardPage> createState() => _DashboardPageState();
}

// Panonun ihtiyaç duyduğu her şeyi tek seferde toplayan sonuç nesnesi.
class _DashboardData {
  final List<SchemaColumn> schema;
  final int rowCount;
  final double? total;
  final double? average;
  final List<AggBucket> distribution;
  final List<AggBucket> series;

  _DashboardData({
    required this.schema,
    required this.rowCount,
    required this.total,
    required this.average,
    required this.distribution,
    required this.series,
  });
}

class _DashboardPageState extends State<DashboardPage> {
  late Future<_DashboardData> _future;

  // Grafik ayarları. İlk yüklemede şemadan makul varsayılanlarla doldurulur.
  String? _groupCol;
  String? _metricCol;
  String? _dateCol;
  String _op = 'sum';
  String _bucket = 'month';
  int _limit = 10;
  bool _donut = false;

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

  // Sunucudan gelen kova anahtarını (date_trunc çıktısı) eksene sığan kısa etikete çevirir.
  String _seriesLabel(String? key) {
    if (key == null) return '—';
    final d = DateTime.tryParse(key);
    if (d == null) return key;
    return switch (_bucket) {
      'year' => '${d.year}',
      'month' => '${_monthsShort[d.month - 1]} ${d.year % 100}',
      _ => '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}',
    };
  }

  // Seçili işlem `count` ise metrik kolonu gerekmez; diğerlerinde zorunludur.
  bool get _needsMetric => _op != 'count';
  String? get _effectiveMetric => _needsMetric ? _metricCol : null;
  String get _effectiveOp =>
      _needsMetric && _metricCol == null ? 'count' : _op;

  /// Balonlarda/başlıklarda görünen okunur ölçü adı: "toplam tutar" gibi.
  String get _measureLabel => _effectiveOp == 'count'
      ? 'satır adedi'
      : '${_opLabels[_effectiveOp]!.toLowerCase()} $_metricCol';

  Future<_DashboardData> _fetch() async {
    final id = widget.dataset.id;
    final schema = await ApiService.getSchema(id);

    final textCols = schema.where((c) => c.type == 'text').toList();
    final numCols = schema.where((c) => c.type == 'number').toList();
    final dateCols = schema.where((c) => c.type == 'date').toList();

    // İlk yüklemede varsayılanları şemadan seç; kullanıcı sonra değiştirebilir.
    _groupCol ??= textCols.isNotEmpty
        ? textCols.first.name
        : (schema.isNotEmpty ? schema.first.name : null);
    _metricCol ??= numCols.isNotEmpty ? numCols.first.name : null;
    _dateCol ??= dateCols.isNotEmpty ? dateCols.first.name : null;

    // Seçili tarih aralığı → filtre koşulları (tarih kolonu varsa).
    final filters = <String>[];
    if (_dateCol != null && _start != null) {
      filters.add('$_dateCol:gte:${_isoDate(_start!)}');
    }
    if (_dateCol != null && _end != null) {
      filters.add('$_dateCol:lte:${_isoDate(_end!)}');
    }

    // KPI: satır sayısı (filtreye duyarlı, gruplamasız count).
    final countBuckets =
        await ApiService.aggregate(id, op: 'count', filters: filters);
    final rowCount = countBuckets.isNotEmpty ? countBuckets.first.count : 0;

    // KPI: sayısal metrik varsa genel toplam ve ortalama (gruplamasız).
    double? total, average;
    final metric = _metricCol;
    if (metric != null) {
      final sumB = await ApiService.aggregate(id,
          op: 'sum', metric: metric, filters: filters);
      total = sumB.isNotEmpty ? sumB.first.value : 0;
      final avgB = await ApiService.aggregate(id,
          op: 'avg', metric: metric, filters: filters);
      average = avgB.isNotEmpty ? avgB.first.value : 0;
    }

    // Dağılım: seçili gruplama kolonuna göre, değere göre azalan, ilk N grup.
    final distribution = _groupCol != null
        ? await ApiService.aggregate(id,
            groupBy: _groupCol,
            op: _effectiveOp,
            metric: _effectiveMetric,
            sort: 'value',
            dir: 'desc',
            limit: _limit,
            filters: filters)
        : <AggBucket>[];

    // Zaman serisi: tarih kolonu varsa kovalanmış (gün/hafta/ay/yıl) ve
    // tarihe göre ARTAN sıralı — çizgi soldan sağa akmalı.
    final series = _dateCol != null
        ? await ApiService.aggregate(id,
            groupBy: _dateCol,
            op: _effectiveOp,
            metric: _effectiveMetric,
            bucket: _bucket,
            sort: 'key',
            dir: 'asc',
            limit: 200,
            filters: filters)
        : <AggBucket>[];

    return _DashboardData(
      schema: schema,
      rowCount: rowCount,
      total: total,
      average: average,
      distribution: distribution,
      series: series,
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
        _controlBar(d),
        const SizedBox(height: 16),
        _kpis(d),
        const SizedBox(height: 16),
        _distributionCard(d),
        if (_dateCol != null) ...[
          const SizedBox(height: 16),
          _seriesCard(d),
        ],
      ],
    );
  }

  // Ölçü seçimi + tarih aralığı: panonun bütün grafiklerini birlikte etkiler.
  Widget _controlBar(_DashboardData d) {
    final textCols = d.schema
        .where((c) => c.type == 'text')
        .map((c) => c.name)
        .toList();
    final groupChoices = textCols.isNotEmpty
        ? textCols
        : d.schema.map((c) => c.name).toList();
    final numCols =
        d.schema.where((c) => c.type == 'number').map((c) => c.name).toList();

    return Card(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
        child: Wrap(
          spacing: 12,
          runSpacing: 12,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            const IconBadge(
                icon: Icons.tune, color: AppColors.brand, size: 32),
            _Picker<String>(
              label: 'Grupla',
              value: _groupCol,
              items: {for (final c in groupChoices) c: c},
              onChanged: (v) {
                _groupCol = v;
                _reload();
              },
            ),
            _Picker<String>(
              label: 'Ölçü',
              value: _op,
              items: _opLabels,
              onChanged: (v) {
                _op = v!;
                _reload();
              },
            ),
            if (_needsMetric)
              _Picker<String>(
                label: 'Kolon',
                value: _metricCol,
                items: {for (final c in numCols) c: c},
                // Sayısal kolonu olmayan veri setinde toplam/ortalama anlamsız.
                emptyHint: 'sayısal kolon yok',
                onChanged: (v) {
                  _metricCol = v;
                  _reload();
                },
              ),
            _Picker<int>(
              label: 'İlk',
              value: _limit,
              items: const {5: '5 grup', 10: '10 grup', 20: '20 grup'},
              onChanged: (v) {
                _limit = v!;
                _reload();
              },
            ),
            if (_dateCol != null) ...[
              const SizedBox(width: 4),
              OutlinedButton.icon(
                onPressed: () => _pickDate(isStart: true),
                icon: const Icon(Icons.event_outlined, size: 16),
                label: Text(_start == null ? 'Başlangıç' : _fmtDate(_start!)),
              ),
              OutlinedButton.icon(
                onPressed: () => _pickDate(isStart: false),
                icon: const Icon(Icons.event_outlined, size: 16),
                label: Text(_end == null ? 'Bitiş' : _fmtDate(_end!)),
              ),
              if (_start != null || _end != null)
                TextButton.icon(
                  onPressed: _clearDates,
                  icon: const Icon(Icons.close, size: 16),
                  label: const Text('Filtreyi kaldır'),
                ),
            ],
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
        value: formatNumber(d.rowCount.toDouble()),
        hint: _start != null || _end != null ? 'seçili aralıkta' : 'tüm veri',
        icon: Icons.table_rows_outlined,
        color: AppColors.brand,
      ),
      if (_metricCol != null) ...[
        StatTile(
          label: 'Toplam $_metricCol',
          value: formatNumber(d.total),
          hint: 'tüm satırların toplamı',
          icon: Icons.functions,
          color: AppColors.accent,
        ),
        StatTile(
          label: 'Ortalama $_metricCol',
          value: formatNumber(d.average),
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
          children: tiles.map((t) => SizedBox(width: width, child: t)).toList(),
        );
      },
    );
  }

  // Dağılım: grup bazında çubuk ya da halka. Aynı veri, iki okuma biçimi —
  // çubuk büyüklükleri karşılaştırır, halka bütün içindeki payı gösterir.
  Widget _distributionCard(_DashboardData d) {
    final data = [
      for (final b in d.distribution)
        ChartDatum(b.key ?? '—', b.value ?? b.count.toDouble()),
    ];

    return SectionCard(
      title: '${_groupCol ?? '—'} bazında ${_opLabels[_effectiveOp]!.toLowerCase()}',
      subtitle: 'En yüksek $_limit grup · $_measureLabel',
      trailing: SegmentedButton<bool>(
        segments: const [
          ButtonSegment(
              value: false,
              icon: Icon(Icons.bar_chart, size: 18),
              tooltip: 'Çubuk grafik'),
          ButtonSegment(
              value: true,
              icon: Icon(Icons.donut_large, size: 18),
              tooltip: 'Halka grafik'),
        ],
        selected: {_donut},
        showSelectedIcon: false,
        onSelectionChanged: (s) => setState(() => _donut = s.first),
        style: const ButtonStyle(
          visualDensity: VisualDensity.compact,
          tapTargetSize: MaterialTapTargetSize.shrinkWrap,
        ),
      ),
      child: _donut
          ? AppDonutChart(data: data, centerLabel: _measureLabel)
          : AppBarChart(data: data, valueLabel: _measureLabel),
    );
  }

  // Zaman serisi: tarih kolonu gün/hafta/ay/yıl kovalarına bölünüp aynı ölçüyle özetlenir.
  Widget _seriesCard(_DashboardData d) {
    final data = [
      for (final b in d.series)
        ChartDatum(_seriesLabel(b.key), b.value ?? b.count.toDouble()),
    ];

    return SectionCard(
      title: 'Zaman içinde $_measureLabel',
      subtitle: '$_dateCol kolonu · ${_bucketLabels[_bucket]!.toLowerCase()} bazında',
      trailing: _Picker<String>(
        label: 'Aralık',
        value: _bucket,
        items: _bucketLabels,
        onChanged: (v) {
          _bucket = v!;
          _reload();
        },
      ),
      child: AppLineChart(data: data, valueLabel: _measureLabel),
    );
  }
}

/// Grafik ayarları için küçük, etiketli açılır liste. Material'ın varsayılan
/// DropdownButtonFormField'ı bir form alanı kadar yer kaplıyordu; bu kompakt sürüm
/// tek satıra "etiket: değer" olarak sığar.
class _Picker<T> extends StatelessWidget {
  final String label;
  final T? value;
  final Map<T, String> items;
  final ValueChanged<T?> onChanged;
  final String? emptyHint;

  const _Picker({
    required this.label,
    required this.value,
    required this.items,
    required this.onChanged,
    this.emptyHint,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.only(left: 12, right: 6),
      decoration: BoxDecoration(
        color: AppColors.surfaceAlt,
        borderRadius: BorderRadius.circular(AppRadius.control),
        border: Border.all(color: AppColors.border),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(label, style: Theme.of(context).textTheme.labelMedium),
          const SizedBox(width: 8),
          if (items.isEmpty)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 12, horizontal: 4),
              child: Text(emptyHint ?? '—',
                  style: Theme.of(context).textTheme.bodySmall),
            )
          else
            DropdownButton<T>(
              value: items.containsKey(value) ? value : null,
              items: [
                for (final e in items.entries)
                  DropdownMenuItem(value: e.key, child: Text(e.value)),
              ],
              onChanged: onChanged,
              underline: const SizedBox.shrink(),
              isDense: true,
              borderRadius: BorderRadius.circular(AppRadius.control),
              dropdownColor: AppColors.surface,
              padding: const EdgeInsets.symmetric(vertical: 10),
              style: Theme.of(context).textTheme.titleSmall,
              icon: const Icon(Icons.expand_more,
                  size: 18, color: AppColors.muted),
            ),
        ],
      ),
    );
  }
}
