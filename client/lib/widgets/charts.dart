import 'dart:math' as math;

import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';

import '../theme/app_theme.dart';

// Panodaki bütün grafikler burada tanımlanır. Ekranlar fl_chart'ı doğrudan tanımaz;
// yalnız `ChartDatum` listesi (etiket + değer) verir. Böylece grafik kütüphanesi
// değişirse ya da bir seri rengi/eksen biçimi düzeltilecekse tek dosya yeter.
//
// Adım 4'teki elle çizilmiş Container çubukların yerini alır: artık eksenler, ızgara,
// dokunma balonları ve animasyon kütüphaneden gelir.

/// Grafiğe giren tek bir nokta: bir grup adı ve onun sayısal değeri.
class ChartDatum {
  final String label;
  final double value;

  const ChartDatum(this.label, this.value);
}

/// 7100 → "7.100", 1183.33 → "1.183,3" (Türkçe ayraçlar, en fazla bir ondalık).
String formatNumber(double? v) {
  if (v == null) return '—';
  final text =
      v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);
  final neg = text.startsWith('-');
  final parts = (neg ? text.substring(1) : text).split('.');
  final intPart = parts.first
      .replaceAllMapped(RegExp(r'(\d)(?=(\d{3})+$)'), (m) => '${m[1]}.');
  final body = parts.length > 1 ? '$intPart,${parts[1]}' : intPart;
  return neg ? '-$body' : body;
}

/// Bir eksenin tavanı, aralık adımı ve etiket biçimi.
///
/// Ham en yüksek değeri doğrudan tavan yapmak iki soruna yol açıyordu: etiketler
/// yuvarlaksız çıkıyordu ("6.957,5") ve her etiket kendi büyüklüğüne göre birim
/// seçtiğinden aynı eksende "10 B" ile "8.000" yan yana düşüyordu. Bu yüzden tavan
/// 1/2/2,5/5 × 10ⁿ adımlarına yuvarlanır ve birim eksenin tamamı için TEK kez seçilir.
class AxisScale {
  final double max;
  final double interval;
  final String Function(double) format;

  const AxisScale(this.max, this.interval, this.format);

  factory AxisScale.forMax(double rawMax) {
    if (rawMax <= 0) return AxisScale(1, 0.25, formatNumber);

    // ~4 aralık hedefle; kaba adımı "güzel" bir sayıya yuvarla.
    final rough = rawMax / 4;
    final mag = math.pow(10, (math.log(rough) / math.ln10).floor()).toDouble();
    final norm = rough / mag;
    final step = (norm <= 1
            ? 1.0
            : norm <= 2
                ? 2.0
                : norm <= 2.5
                    ? 2.5
                    : norm <= 5
                        ? 5.0
                        : 10.0) *
        mag;

    // En yüksek çubuk/nokta tavana yapışmasın diye tam denk gelirse bir adım eklenir.
    var max = (rawMax / step).ceil() * step;
    if (max <= rawMax) max += step;

    // Birim tavana göre seçilir → eksendeki bütün etiketler aynı birimde okunur.
    final format = max >= 1e9
        ? (double v) => '${formatNumber(v / 1e9)} Mr'
        : max >= 1e6
            ? (double v) => '${formatNumber(v / 1e6)} Mn'
            : max >= 1e5
                ? (double v) => '${formatNumber(v / 1e3)} B'
                : formatNumber;

    return AxisScale(max, step, format);
  }
}

/// Grafiklerin ortak yerleşimi: sabit yükseklik + veri yoksa açıklayıcı boşluk.
class _ChartFrame extends StatelessWidget {
  final double height;
  final bool isEmpty;
  final Widget child;

  const _ChartFrame({
    required this.height,
    required this.isEmpty,
    required this.child,
  });

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      height: height,
      child: isEmpty
          ? const Center(
              child: Text('Bu seçimde gösterilecek veri yok.',
                  style: TextStyle(color: AppColors.muted, fontSize: 13)),
            )
          : child,
    );
  }
}

// Izgara: yalnız yatay çizgiler, kart kenarıyla aynı sönük renkte — veri öne çıksın.
// Aralık eksen ölçeğiyle aynı olmalı, yoksa çizgiler etiketlerle hizalanmaz.
FlGridData _grid(double interval) => FlGridData(
      show: true,
      drawVerticalLine: false,
      horizontalInterval: interval,
      getDrawingHorizontalLine: (_) =>
          const FlLine(color: AppColors.border, strokeWidth: 1),
    );

const _axisStyle = TextStyle(color: AppColors.muted, fontSize: 11);

/// Dikey çubuk grafik: her grup bir çubuk, renkler paletten sırayla gelir.
/// Çubuğun arkasında sönük bir "yol" durur (en yüksek değere göre doluluk hissi verir).
class AppBarChart extends StatelessWidget {
  final List<ChartDatum> data;

  /// Balonda değerin yanında yazılacak ad ("toplam tutar" gibi).
  final String? valueLabel;
  final double height;

  const AppBarChart({
    super.key,
    required this.data,
    this.valueLabel,
    this.height = 280,
  });

  @override
  Widget build(BuildContext context) {
    final maxV =
        data.fold<double>(0, (a, d) => d.value.abs() > a ? d.value.abs() : a);
    final axis = AxisScale.forMax(maxV);
    // Çok grup varsa etiketler yan yana sığmaz → eğik yazılır.
    final tilt = data.length > 6;

    return _ChartFrame(
      height: height,
      isEmpty: data.isEmpty,
      child: BarChart(
        BarChartData(
          alignment: BarChartAlignment.spaceAround,
          maxY: axis.max,
          gridData: _grid(axis.interval),
          borderData: FlBorderData(show: false),
          barTouchData: BarTouchData(
            touchTooltipData: BarTouchTooltipData(
              getTooltipColor: (_) => AppColors.surfaceAlt,
              tooltipPadding:
                  const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              fitInsideHorizontally: true,
              fitInsideVertically: true,
              getTooltipItem: (group, groupIndex, rod, rodIndex) =>
                  BarTooltipItem(
                '${data[groupIndex].label}\n',
                const TextStyle(
                    color: AppColors.muted,
                    fontSize: 11.5,
                    fontWeight: FontWeight.w600),
                children: [
                  TextSpan(
                    text: formatNumber(rod.toY),
                    style: const TextStyle(
                        color: AppColors.text,
                        fontSize: 14,
                        fontWeight: FontWeight.w700),
                  ),
                  if (valueLabel != null)
                    TextSpan(
                      text: '  $valueLabel',
                      style: const TextStyle(
                          color: AppColors.muted, fontSize: 11),
                    ),
                ],
              ),
            ),
          ),
          titlesData: FlTitlesData(
            topTitles: const AxisTitles(),
            rightTitles: const AxisTitles(),
            leftTitles: AxisTitles(
              sideTitles: SideTitles(
                showTitles: true,
                reservedSize: 52,
                interval: axis.interval,
                getTitlesWidget: (value, meta) => Padding(
                  padding: const EdgeInsets.only(right: 8),
                  child: Text(axis.format(value),
                      style: _axisStyle, textAlign: TextAlign.right),
                ),
              ),
            ),
            bottomTitles: AxisTitles(
              sideTitles: SideTitles(
                showTitles: true,
                reservedSize: tilt ? 62 : 34,
                getTitlesWidget: (value, meta) {
                  final i = value.toInt();
                  if (i < 0 || i >= data.length) return const SizedBox.shrink();
                  final label = Text(
                    data[i].label,
                    style: _axisStyle,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  );
                  return SideTitleWidget(
                    meta: meta,
                    space: 8,
                    child: tilt
                        ? Transform.rotate(
                            angle: -0.6,
                            alignment: Alignment.topRight,
                            child: SizedBox(width: 74, child: label),
                          )
                        : SizedBox(width: 88, child: Center(child: label)),
                  );
                },
              ),
            ),
          ),
          barGroups: [
            for (var i = 0; i < data.length; i++)
              BarChartGroupData(
                x: i,
                barRods: [
                  BarChartRodData(
                    toY: data[i].value,
                    width: data.length > 8 ? 16 : 26,
                    borderRadius: BorderRadius.circular(6),
                    // Degrade: çubuğun tabanı sönük, tepesi canlı.
                    gradient: LinearGradient(
                      begin: Alignment.bottomCenter,
                      end: Alignment.topCenter,
                      colors: [
                        chartPalette[i % chartPalette.length]
                            .withValues(alpha: 0.55),
                        chartPalette[i % chartPalette.length],
                      ],
                    ),
                    backDrawRodData: BackgroundBarChartRodData(
                      show: true,
                      toY: axis.max,
                      color: AppColors.surfaceAlt.withValues(alpha: 0.55),
                    ),
                  ),
                ],
              ),
          ],
        ),
      ),
    );
  }
}

/// Gruplanmış çubuk grafiğin tek bir serisi: ad + her gruptaki değeri.
///
/// `values` uzunluğu grup sayısıyla AYNI olmalıdır; o grupta bu serinin kaydı yoksa
/// null geçilir. Sunucu yalnız var olan kombinasyonları döndürdüğü için boşlukları
/// çağıran taraf doldurur (bkz. AppGroupedBarChart.fromBuckets).
class ChartSeries {
  final String name;
  final List<double?> values;

  const ChartSeries(this.name, this.values);
}

/// İki kolonla gruplanmış çubuk grafik: her grup yan yana birkaç çubuk.
/// "Şehir VE kategoriye göre toplam" → x ekseninde şehirler, her şehirde kategori
/// başına bir çubuk. Renk seriyi (kategoriyi) anlatır, bu yüzden lejant zorunlu.
///
/// Tek gruplamalı AppBarChart'tan ayrı bir widget: orada renk yalnız çubukları
/// birbirinden ayırmak içindir ve hiçbir anlam taşımaz; burada renk VERİDİR.
class AppGroupedBarChart extends StatelessWidget {
  final List<String> groups;
  final List<ChartSeries> series;
  final String? valueLabel;
  final double height;

  const AppGroupedBarChart({
    super.key,
    required this.groups,
    required this.series,
    this.valueLabel,
    this.height = 300,
  });

  @override
  Widget build(BuildContext context) {
    final maxV = series.fold<double>(
        0,
        (a, s) => s.values.fold<double>(
            a, (b, v) => (v?.abs() ?? 0) > b ? v!.abs() : b));
    final axis = AxisScale.forMax(maxV);
    final tilt = groups.length > 5;

    // Çubuk kalınlığı toplam çubuk sayısına göre: kalabalıkta incelmezse taşar.
    final rodCount = groups.length * math.max(series.length, 1);
    final barWidth = rodCount > 24
        ? 8.0
        : rodCount > 12
            ? 12.0
            : 18.0;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _SeriesLegend(names: series.map((s) => s.name).toList()),
        const SizedBox(height: 12),
        _ChartFrame(
          height: height,
          isEmpty: groups.isEmpty || series.isEmpty,
          child: BarChart(
            BarChartData(
              alignment: BarChartAlignment.spaceAround,
              maxY: axis.max,
              gridData: _grid(axis.interval),
              borderData: FlBorderData(show: false),
              barTouchData: BarTouchData(
                touchTooltipData: BarTouchTooltipData(
                  getTooltipColor: (_) => AppColors.surfaceAlt,
                  tooltipPadding:
                      const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                  fitInsideHorizontally: true,
                  fitInsideVertically: true,
                  // Balonda hem grup hem seri yazılır: renkten seriyi hatırlamak
                  // zorunda kalmasın diye ("Ankara · Elektronik").
                  getTooltipItem: (group, groupIndex, rod, rodIndex) =>
                      BarTooltipItem(
                    '${groups[groupIndex]} · ${series[rodIndex].name}\n',
                    const TextStyle(
                        color: AppColors.muted,
                        fontSize: 11.5,
                        fontWeight: FontWeight.w600),
                    children: [
                      TextSpan(
                        text: formatNumber(rod.toY),
                        style: const TextStyle(
                            color: AppColors.text,
                            fontSize: 14,
                            fontWeight: FontWeight.w700),
                      ),
                      if (valueLabel != null)
                        TextSpan(
                          text: '  $valueLabel',
                          style: const TextStyle(
                              color: AppColors.muted, fontSize: 11),
                        ),
                    ],
                  ),
                ),
              ),
              titlesData: FlTitlesData(
                topTitles: const AxisTitles(),
                rightTitles: const AxisTitles(),
                leftTitles: AxisTitles(
                  sideTitles: SideTitles(
                    showTitles: true,
                    reservedSize: 52,
                    interval: axis.interval,
                    getTitlesWidget: (value, meta) => Padding(
                      padding: const EdgeInsets.only(right: 8),
                      child: Text(axis.format(value),
                          style: _axisStyle, textAlign: TextAlign.right),
                    ),
                  ),
                ),
                bottomTitles: AxisTitles(
                  sideTitles: SideTitles(
                    showTitles: true,
                    reservedSize: tilt ? 62 : 34,
                    getTitlesWidget: (value, meta) {
                      final i = value.toInt();
                      if (i < 0 || i >= groups.length) {
                        return const SizedBox.shrink();
                      }
                      final label = Text(
                        groups[i],
                        style: _axisStyle,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      );
                      return SideTitleWidget(
                        meta: meta,
                        space: 8,
                        child: tilt
                            ? Transform.rotate(
                                angle: -0.6,
                                alignment: Alignment.topRight,
                                child: SizedBox(width: 74, child: label),
                              )
                            : SizedBox(width: 88, child: Center(child: label)),
                      );
                    },
                  ),
                ),
              ),
              barGroups: [
                for (var g = 0; g < groups.length; g++)
                  BarChartGroupData(
                    x: g,
                    barsSpace: 4,
                    barRods: [
                      for (var s = 0; s < series.length; s++)
                        BarChartRodData(
                          // Kaydı olmayan kombinasyon 0 yükseklikte çizilir:
                          // "bu şehirde bu kategori yok" zaten boşluk demektir.
                          toY: series[s].values[g] ?? 0,
                          width: barWidth,
                          borderRadius: BorderRadius.circular(4),
                          gradient: LinearGradient(
                            begin: Alignment.bottomCenter,
                            end: Alignment.topCenter,
                            colors: [
                              chartPalette[s % chartPalette.length]
                                  .withValues(alpha: 0.55),
                              chartPalette[s % chartPalette.length],
                            ],
                          ),
                        ),
                    ],
                  ),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

/// Seri adı → renk eşlemesi. Gruplanmış grafikte renk anlam taşıdığı için şart.
class _SeriesLegend extends StatelessWidget {
  final List<String> names;

  const _SeriesLegend({required this.names});

  @override
  Widget build(BuildContext context) {
    return Wrap(
      spacing: 16,
      runSpacing: 8,
      children: [
        for (var i = 0; i < names.length; i++)
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                width: 10,
                height: 10,
                decoration: BoxDecoration(
                  color: chartPalette[i % chartPalette.length],
                  borderRadius: BorderRadius.circular(3),
                ),
              ),
              const SizedBox(width: 7),
              Text(names[i],
                  style: Theme.of(context).textTheme.bodySmall,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis),
            ],
          ),
      ],
    );
  }
}

/// Zaman serisi çizgisi: eğrisel çizgi + altında degrade dolgu.
/// X ekseni tarih kovalarının sırasıdır; etiketler kalabalıklaşmasın diye seyreltilir.
class AppLineChart extends StatelessWidget {
  final List<ChartDatum> data;
  final String? valueLabel;
  final double height;
  final Color color;

  const AppLineChart({
    super.key,
    required this.data,
    this.valueLabel,
    this.height = 260,
    this.color = AppColors.accent,
  });

  @override
  Widget build(BuildContext context) {
    final maxV = data.fold<double>(0, (a, d) => d.value > a ? d.value : a);
    final minV =
        data.fold<double>(double.infinity, (a, d) => d.value < a ? d.value : a);
    // Değerler eksiye inmiyorsa taban sıfırdır — büyüklükler göz kararı karşılaştırılabilsin.
    final negative = data.isNotEmpty && minV < 0;
    final axis = AxisScale.forMax(negative ? maxV - minV : maxV);
    final low = negative ? minV * 1.1 : 0.0;

    // En fazla ~6 tarih etiketi göster; gerisi atlanır.
    final step = (data.length / 6).ceil().clamp(1, 999);

    return _ChartFrame(
      height: height,
      isEmpty: data.isEmpty,
      child: LineChart(
        LineChartData(
          minY: low,
          maxY: negative ? low + axis.max : axis.max,
          gridData: _grid(axis.interval),
          borderData: FlBorderData(show: false),
          lineTouchData: LineTouchData(
            touchTooltipData: LineTouchTooltipData(
              getTooltipColor: (_) => AppColors.surfaceAlt,
              tooltipPadding:
                  const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              fitInsideHorizontally: true,
              fitInsideVertically: true,
              getTooltipItems: (spots) => [
                for (final s in spots)
                  LineTooltipItem(
                    '${data[s.x.toInt()].label}\n',
                    const TextStyle(
                        color: AppColors.muted,
                        fontSize: 11.5,
                        fontWeight: FontWeight.w600),
                    children: [
                      TextSpan(
                        text: formatNumber(s.y),
                        style: const TextStyle(
                            color: AppColors.text,
                            fontSize: 14,
                            fontWeight: FontWeight.w700),
                      ),
                      if (valueLabel != null)
                        TextSpan(
                          text: '  $valueLabel',
                          style: const TextStyle(
                              color: AppColors.muted, fontSize: 11),
                        ),
                    ],
                  ),
              ],
            ),
          ),
          titlesData: FlTitlesData(
            topTitles: const AxisTitles(),
            rightTitles: const AxisTitles(),
            leftTitles: AxisTitles(
              sideTitles: SideTitles(
                showTitles: true,
                reservedSize: 52,
                interval: axis.interval,
                getTitlesWidget: (value, meta) => Padding(
                  padding: const EdgeInsets.only(right: 8),
                  child: Text(axis.format(value),
                      style: _axisStyle, textAlign: TextAlign.right),
                ),
              ),
            ),
            bottomTitles: AxisTitles(
              sideTitles: SideTitles(
                showTitles: true,
                reservedSize: 34,
                interval: 1,
                getTitlesWidget: (value, meta) {
                  final i = value.toInt();
                  if (i < 0 || i >= data.length) return const SizedBox.shrink();
                  // Son etiket her zaman görünsün, aradakiler adım adım.
                  if (i % step != 0 && i != data.length - 1) {
                    return const SizedBox.shrink();
                  }
                  return SideTitleWidget(
                    meta: meta,
                    space: 8,
                    child: Text(data[i].label, style: _axisStyle),
                  );
                },
              ),
            ),
          ),
          lineBarsData: [
            LineChartBarData(
              spots: [
                for (var i = 0; i < data.length; i++)
                  FlSpot(i.toDouble(), data[i].value),
              ],
              isCurved: true,
              curveSmoothness: 0.28,
              preventCurveOverShooting: true,
              barWidth: 3,
              color: color,
              // Tek nokta varsa çizgi görünmez → noktayı her zaman göster.
              dotData: FlDotData(
                show: true,
                getDotPainter: (spot, percent, bar, index) =>
                    FlDotCirclePainter(
                  radius: 3.5,
                  color: color,
                  strokeWidth: 2,
                  strokeColor: AppColors.surface,
                ),
              ),
              belowBarData: BarAreaData(
                show: true,
                gradient: LinearGradient(
                  begin: Alignment.topCenter,
                  end: Alignment.bottomCenter,
                  colors: [
                    color.withValues(alpha: 0.28),
                    color.withValues(alpha: 0.0),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Halka (donut) grafik: payların bütün içindeki oranını gösterir.
/// Ortada toplam yazar, sağında/altında tıklanabilir gösterge listesi durur.
class AppDonutChart extends StatefulWidget {
  final List<ChartDatum> data;
  final String? centerLabel;
  final double height;

  const AppDonutChart({
    super.key,
    required this.data,
    this.centerLabel,
    this.height = 280,
  });

  @override
  State<AppDonutChart> createState() => _AppDonutChartState();
}

class _AppDonutChartState extends State<AppDonutChart> {
  int _touched = -1;

  @override
  Widget build(BuildContext context) {
    final data = widget.data;
    final total = data.fold<double>(0, (a, d) => a + d.value.abs());

    final chart = PieChart(
      PieChartData(
        sectionsSpace: 2,
        centerSpaceRadius: 62,
        startDegreeOffset: -90,
        pieTouchData: PieTouchData(
          touchCallback: (event, response) {
            final index = response?.touchedSection?.touchedSectionIndex ?? -1;
            // Parmak/fare ayrılınca vurgu kalkar.
            setState(() => _touched = event.isInterestedForInteractions ? index : -1);
          },
        ),
        sections: [
          for (var i = 0; i < data.length; i++)
            PieChartSectionData(
              value: data[i].value.abs(),
              color: chartPalette[i % chartPalette.length],
              radius: _touched == i ? 34 : 26,
              // Dilim payı çok küçükse yüzde yazısı okunmaz → gizlenir.
              showTitle: total > 0 && data[i].value.abs() / total >= 0.07,
              title: total > 0
                  ? '%${(data[i].value.abs() / total * 100).toStringAsFixed(0)}'
                  : '',
              titleStyle: const TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w700,
                  color: Color(0xFF0E1420)),
            ),
        ],
      ),
    );

    final center = Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          _touched >= 0 && _touched < data.length
              ? formatNumber(data[_touched].value)
              : formatNumber(total),
          style: Theme.of(context).textTheme.titleLarge,
        ),
        const SizedBox(height: 2),
        Text(
          _touched >= 0 && _touched < data.length
              ? data[_touched].label
              : (widget.centerLabel ?? 'toplam'),
          style: Theme.of(context).textTheme.bodySmall,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
      ],
    );

    return _ChartFrame(
      height: widget.height,
      isEmpty: data.isEmpty || total == 0,
      child: LayoutBuilder(
        builder: (context, c) {
          final ring = Stack(
            alignment: Alignment.center,
            children: [chart, center],
          );
          final legend = ChartLegend(
            data: data,
            highlighted: _touched,
            total: total,
            onHover: (i) => setState(() => _touched = i),
          );

          // Geniş kartta halka solda, gösterge sağda; dar kartta gösterge altta.
          if (c.maxWidth >= 520) {
            return Row(
              children: [
                Expanded(flex: 5, child: ring),
                const SizedBox(width: 20),
                Expanded(flex: 4, child: SingleChildScrollView(child: legend)),
              ],
            );
          }
          return Column(
            children: [
              Expanded(child: ring),
              const SizedBox(height: 12),
              SizedBox(height: 74, child: SingleChildScrollView(child: legend)),
            ],
          );
        },
      ),
    );
  }
}

/// Halka grafiğin yanındaki liste: renk noktası + grup adı + değer/oran.
class ChartLegend extends StatelessWidget {
  final List<ChartDatum> data;
  final int highlighted;
  final double total;
  final ValueChanged<int>? onHover;

  const ChartLegend({
    super.key,
    required this.data,
    required this.total,
    this.highlighted = -1,
    this.onHover,
  });

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        for (var i = 0; i < data.length; i++)
          MouseRegion(
            onEnter: (_) => onHover?.call(i),
            onExit: (_) => onHover?.call(-1),
            child: Container(
              padding:
                  const EdgeInsets.symmetric(horizontal: 8, vertical: 5),
              margin: const EdgeInsets.only(bottom: 2),
              decoration: BoxDecoration(
                color: highlighted == i
                    ? AppColors.surfaceAlt
                    : Colors.transparent,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                children: [
                  Container(
                    width: 10,
                    height: 10,
                    decoration: BoxDecoration(
                      color: chartPalette[i % chartPalette.length],
                      borderRadius: BorderRadius.circular(3),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      data[i].label,
                      style: Theme.of(context).textTheme.bodyMedium,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  const SizedBox(width: 8),
                  Text(
                    formatNumber(data[i].value),
                    style: const TextStyle(
                        fontSize: 12.5, fontWeight: FontWeight.w600),
                  ),
                  if (total > 0) ...[
                    const SizedBox(width: 8),
                    SizedBox(
                      width: 42,
                      child: Text(
                        '%${(data[i].value.abs() / total * 100).toStringAsFixed(0)}',
                        textAlign: TextAlign.right,
                        style: const TextStyle(
                            fontSize: 12, color: AppColors.muted),
                      ),
                    ),
                  ],
                ],
              ),
            ),
          ),
      ],
    );
  }
}
