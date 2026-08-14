import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../api_service.dart';
import '../platform/platform.dart';
import '../theme/app_theme.dart';
import '../widgets/ui.dart';

// Belgeden veri girişi — CSV/Excel'in yanındaki ÜÇÜNCÜ kapı.
//
// Ekranın varlık sebebi tek cümleyle: model %100 doğru okumuyor, o yüzden okuduğu şey
// doğrudan kaydedilmiyor. Kullanıcı belgeyi ve çıkarımı YAN YANA görüp düzeltiyor,
// kaydetme kararını o veriyor. Ölçümde alan doğruluğu 8/8'e kadar çıktı ama kalem
// tablosunda %94'te kaldı; kalan %6 gözle yakalanmazsa sessizce veriye karışır.
//
// Akış iki yoldan biriyle başlar:
//   hedef set BELLİ  → şemalı çıkarım (istem şemayı dayatır, görüntüye başlık şeridi eklenir)
//   hedef set BELİRSİZ → keşif geçişi (adları model seçer, sunucu var olan setlerle eşleştirir)

enum _Step { pick, reading, review, saving }

class DocumentPage extends StatefulWidget {
  /// Kaydetme başarılıysa çağrılır — veri seti listesi/tablosu tazelensin diye.
  final VoidCallback? onSaved;

  const DocumentPage({super.key, this.onSaved});

  @override
  State<DocumentPage> createState() => _DocumentPageState();
}

class _DocumentPageState extends State<DocumentPage> {
  _Step _step = _Step.pick;

  List<Dataset> _datasets = [];
  bool _loadingDatasets = true;

  /// null → keşif geçişi. Kullanıcı "hangi sete ait bilmiyorum" dediğinde böyle kalır.
  String? _targetId;

  PickedFile? _file;
  DocumentExtraction? _result;

  /// Tablonun DÜZENLENEBİLİR kopyası. Sunucudan gelen `_result.rows` dokunulmadan
  /// duruyor; kullanıcının neyi değiştirdiği böyle görülebiliyor.
  List<String> _columns = [];
  List<List<String>> _rows = [];

  String? _error;

  @override
  void initState() {
    super.initState();
    _loadDatasets();
  }

  Future<void> _loadDatasets() async {
    try {
      final list = await ApiService.getDatasets();
      if (!mounted) return;
      setState(() {
        _datasets = list;
        _loadingDatasets = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() => _loadingDatasets = false);
    }
  }

  // ---- adım 1: belgeyi oku ----

  Future<void> _pickAndRead() async {
    PickedFile? file;
    try {
      file = await pickImageFile();
    } catch (e) {
      setState(() => _error = e.toString());
      return;
    }
    if (file == null) return; // kullanıcı iptal etti

    setState(() {
      _file = file;
      _step = _Step.reading;
      _error = null;
    });

    await _read();
  }

  Future<void> _read() async {
    final file = _file!;
    try {
      final result = _targetId == null
          ? await ApiService.discoverDocument(file.bytes, file.name)
          : await ApiService.extractDocument(_targetId!, file.bytes, file.name);

      if (!mounted) return;
      setState(() {
        _result = result;
        _columns = List.of(result.columns);
        _rows = result.rows.map(List<String>.of).toList();
        _step = _Step.review;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.toString();
        _step = _Step.pick;
      });
    }
  }

  // Keşiften çıkan öneriye tıklanınca belge o setin ŞEMASIYLA YENİDEN okunur.
  //
  // Neden ikinci bir model çağrısı göze alınıyor: ilk geçişte model adları kendi seçti ve
  // görüntüye başlık şeridi eklenemedi (şerit şemadan üretiliyor). Şema belli olunca
  // ikisi de devreye giriyor; ölçümde kalem doğruluğu %51'den %94'e bu şekilde çıkmıştı.
  Future<void> _reReadWithSchema(String datasetId) async {
    setState(() {
      _targetId = datasetId;
      _step = _Step.reading;
    });
    await _read();
  }

  // ---- adım 2: kaydet ----

  Future<void> _save() async {
    setState(() {
      _step = _Step.saving;
      _error = null;
    });

    try {
      final target = _targetId;
      final int saved;

      if (target != null) {
        saved = await ApiService.confirmDocument(target, _columns, _rows);
      } else {
        // Yeni set: tabloyu CSV'ye çevirip VAR OLAN yükleme yolundan geçiriyoruz.
        // Böylece şema algılama ve satır doğrulama, dosyadan gelen veriyle birebir
        // aynı koddan geçiyor — belgeye özel ikinci bir içe aktarma yolu doğmuyor.
        await ApiService.uploadDataset(
          name: _result?.suggestedName ?? 'Belgeden gelen veriler',
          bytes: utf8.encode(_toCsv()),
          filename: 'belge.csv',
        );
        saved = _rows.length;
      }

      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('$saved satır kaydedildi.')),
      );
      widget.onSaved?.call();
      _reset();
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.toString();
        _step = _Step.review;
      });
    }
  }

  /// Tabloyu CSV'ye çevirir. Tırnak ve ayraç KAÇIRILIR: "Kalem, kutu" gibi bir değer
  /// kaçırılmazsa iki kolona bölünür ve satır sessizce kayar.
  String _toCsv() {
    String cell(String v) => '"${v.replaceAll('"', '""')}"';
    final buffer = StringBuffer()..writeln(_columns.map(cell).join(','));
    for (final row in _rows) {
      buffer.writeln(row.map(cell).join(','));
    }
    return buffer.toString();
  }

  void _reset() => setState(() {
        _step = _Step.pick;
        _file = null;
        _result = null;
        _columns = [];
        _rows = [];
        _error = null;
      });

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const PageHeader(
          title: 'Belgeden veri',
          subtitle: 'Fatura, fiş veya makbuz fotoğrafını okut; kaydetmeden önce kontrol et.',
        ),
        const SizedBox(height: 16),
        if (_error != null) ...[
          _ErrorBanner(message: _error!, onClose: () => setState(() => _error = null)),
          const SizedBox(height: 12),
        ],
        Expanded(child: _body),
      ],
    );
  }

  Widget get _body => switch (_step) {
        _Step.pick => _picker,
        _Step.reading => const LoadingView(
            message: 'Belge okunuyor… Görsel model belge başına 30-45 saniye sürebilir.'),
        _ => _review,
      };

  // ---- ekran: kaynak seçimi ----

  Widget get _picker {
    if (_loadingDatasets) return const LoadingView();

    return SingleChildScrollView(
      child: SectionCard(
        title: 'Belge nereye yazılacak?',
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              'Hedef veri setini seçersen belge o setin kolonlarına göre okunur ve '
              'doğruluk belirgin biçimde artar. Emin değilsen boş bırak: sistem belgeyi '
              'okuyup hangi sete uyduğunu kendisi önerir.',
              style: TextStyle(color: AppColors.muted, height: 1.5),
            ),
            const SizedBox(height: 16),
            DropdownButtonFormField<String?>(
              initialValue: _targetId,
              decoration: const InputDecoration(labelText: 'Hedef veri seti'),
              items: [
                const DropdownMenuItem(
                  value: null,
                  child: Text('Bilmiyorum — sistem önersin'),
                ),
                for (final d in _datasets)
                  DropdownMenuItem(value: d.id, child: Text(d.name)),
              ],
              onChanged: (v) => setState(() => _targetId = v),
            ),
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: _pickAndRead,
              icon: const Icon(Icons.upload_file, size: 18),
              label: const Text('Belge görüntüsü seç'),
            ),
            const SizedBox(height: 10),
            const Text(
              '.jpg, .png veya .webp · en fazla 15 MB',
              style: TextStyle(fontSize: 12, color: AppColors.muted),
            ),
          ],
        ),
      ),
    );
  }

  // ---- ekran: onay ----

  Widget get _review {
    final result = _result!;

    // Belge ve çıkarım YAN YANA duruyor. Dar ekranda alt alta düşüyor; ama geniş ekranda
    // yan yana olması şart: kullanıcı eksik satırı ancak belgeye bakarak fark eder.
    return LayoutBuilder(builder: (context, constraints) {
      final wide = constraints.maxWidth > 1000;
      final image = _DocumentPreview(bytes: _file!.bytes, name: _file!.name);
      final table = _ReviewPanel(
        result: result,
        columns: _columns,
        rows: _rows,
        targetId: _targetId,
        saving: _step == _Step.saving,
        onCellChanged: (r, c, v) => setState(() => _rows[r][c] = v),
        onRowRemoved: (r) => setState(() => _rows.removeAt(r)),
        onPickSuggestion: _reReadWithSchema,
        onSave: _rows.isEmpty ? null : _save,
        onCancel: _reset,
      );

      if (!wide) {
        return SingleChildScrollView(
          child: Column(children: [
            SizedBox(height: 320, child: image),
            const SizedBox(height: 16),
            table,
          ]),
        );
      }

      return Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Expanded(flex: 2, child: image),
          const SizedBox(width: 16),
          Expanded(flex: 3, child: SingleChildScrollView(child: table)),
        ],
      );
    });
  }
}

// --- belgenin kendisi -------------------------------------------------------------------

class _DocumentPreview extends StatelessWidget {
  final Uint8List bytes;
  final String name;

  const _DocumentPreview({required this.bytes, required this.name});

  @override
  Widget build(BuildContext context) {
    return SectionCard(
      title: 'Belge',
      subtitle: name,
      child: ClipRRect(
        borderRadius: BorderRadius.circular(AppRadius.control),
        // InteractiveViewer: fişin küçük yazısı ekranda okunmuyor; kullanıcı çıkarımı
        // doğrulayabilmek için belgeye yakınlaşabilmeli.
        child: InteractiveViewer(
          maxScale: 6,
          child: Image.memory(bytes, fit: BoxFit.contain),
        ),
      ),
    );
  }
}

// --- çıkarım + düzeltme -----------------------------------------------------------------

class _ReviewPanel extends StatelessWidget {
  final DocumentExtraction result;
  final List<String> columns;
  final List<List<String>> rows;
  final String? targetId;
  final bool saving;
  final void Function(int row, int col, String value) onCellChanged;
  final void Function(int row) onRowRemoved;
  final void Function(String datasetId) onPickSuggestion;
  final VoidCallback? onSave;
  final VoidCallback onCancel;

  const _ReviewPanel({
    required this.result,
    required this.columns,
    required this.rows,
    required this.targetId,
    required this.saving,
    required this.onCellChanged,
    required this.onRowRemoved,
    required this.onPickSuggestion,
    required this.onSave,
    required this.onCancel,
  });

  @override
  Widget build(BuildContext context) {
    final errors = result.errorIndex;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // Şüpheli çıkarım en üstte ve en görünür yerde: bu uyarıyı kaçıran kullanıcı,
        // yarısı okunmuş bir belgeyi tam sanıp kaydeder.
        if (result.suspect)
          _Banner(
            icon: Icons.warning_amber_rounded,
            color: AppColors.danger,
            title: 'Bu çıkarım güvenilir sayılmıyor',
            message: 'Belge modelin bağlamına sığmadı; bir kısmını hiç görmemiş olabilir. '
                'Değerleri tek tek doğrulayın.',
          ),
        for (final warning in result.warnings)
          _Banner(
            icon: Icons.info_outline,
            color: AppColors.warning,
            title: 'Not',
            message: warning,
          ),

        if (targetId == null) ...[
          const SizedBox(height: 4),
          _Suggestions(result: result, onPick: onPickSuggestion),
        ],

        const SizedBox(height: 12),
        SectionCard(
          title: 'Çıkarılan tablo',
          subtitle: '${rows.length} satır · ${result.model} · '
              '${(result.durationMs / 1000).toStringAsFixed(1)} sn',
          child: Column(
            children: [
              if (errors.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.only(bottom: 12),
                  child: _Banner(
                    icon: Icons.error_outline,
                    color: AppColors.danger,
                    title: '${errors.length} hücre şemaya uymuyor',
                    message: 'İşaretli hücreleri düzeltmeden kayıt yapılamaz.',
                  ),
                ),
              _EditableTable(
                columns: columns,
                rows: rows,
                errors: errors,
                onCellChanged: onCellChanged,
                onRowRemoved: onRowRemoved,
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),
        Row(
          children: [
            FilledButton.icon(
              onPressed: saving ? null : onSave,
              icon: saving
                  ? const ButtonSpinner()
                  : const Icon(Icons.check, size: 18),
              label: Text(targetId == null ? 'Yeni veri seti oluştur' : 'Kaydet'),
            ),
            const SizedBox(width: 10),
            TextButton(onPressed: saving ? null : onCancel, child: const Text('Vazgeç')),
          ],
        ),
      ],
    );
  }
}

// --- keşif önerileri --------------------------------------------------------------------

class _Suggestions extends StatelessWidget {
  final DocumentExtraction result;
  final void Function(String datasetId) onPick;

  const _Suggestions({required this.result, required this.onPick});

  @override
  Widget build(BuildContext context) {
    if (result.matches.isEmpty) {
      return SectionCard(
        title: 'Uyan veri seti bulunamadı',
        child: Text(
          'Bu belge var olan setlerin hiçbirine yeterince benzemiyor. '
          'Aşağıdaki tabloyu onaylarsan "${result.suggestedName}" adıyla yeni bir '
          'veri seti oluşturulur.',
          style: const TextStyle(color: AppColors.muted, height: 1.5),
        ),
      );
    }

    return SectionCard(
      title: 'Bu belge şu veri setine ait olabilir',
      subtitle: 'Birini seçersen belge o setin şemasına göre yeniden okunur (daha doğru).',
      child: Column(
        children: [
          for (final m in result.matches)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: InkWell(
                onTap: () => onPick(m.datasetId),
                borderRadius: BorderRadius.circular(AppRadius.control),
                child: Container(
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: AppColors.surfaceAlt,
                    border: Border.all(color: AppColors.border),
                    borderRadius: BorderRadius.circular(AppRadius.control),
                  ),
                  child: Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(m.name,
                                style: const TextStyle(fontWeight: FontWeight.w600)),
                            const SizedBox(height: 3),
                            Text(
                              [
                                '%${m.percent} benzerlik',
                                '${m.mappings.length} kolon eşleşti',
                                if (m.missingColumns.isNotEmpty)
                                  '${m.missingColumns.length} kolon belgede yok',
                                if (m.extraColumns.isNotEmpty)
                                  '${m.extraColumns.length} kolon sette yok',
                              ].join(' · '),
                              style: const TextStyle(
                                  fontSize: 12, color: AppColors.muted),
                            ),
                          ],
                        ),
                      ),
                      const Icon(Icons.chevron_right, color: AppColors.muted),
                    ],
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

// --- düzenlenebilir tablo ---------------------------------------------------------------

class _EditableTable extends StatelessWidget {
  final List<String> columns;
  final List<List<String>> rows;
  final Map<String, DocumentCellError> errors;
  final void Function(int row, int col, String value) onCellChanged;
  final void Function(int row) onRowRemoved;

  const _EditableTable({
    required this.columns,
    required this.rows,
    required this.errors,
    required this.onCellChanged,
    required this.onRowRemoved,
  });

  @override
  Widget build(BuildContext context) {
    if (rows.isEmpty) {
      return const Padding(
        padding: EdgeInsets.symmetric(vertical: 24),
        child: Text('Kaydedilecek satır kalmadı.',
            style: TextStyle(color: AppColors.muted)),
      );
    }

    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      child: DataTable(
        columnSpacing: 18,
        headingRowHeight: 38,
        dataRowMinHeight: 44,
        dataRowMaxHeight: 52,
        columns: [
          for (final c in columns)
            DataColumn(
                label: Text(c,
                    style: const TextStyle(
                        fontSize: 12.5, fontWeight: FontWeight.w600))),
          const DataColumn(label: Text('')),
        ],
        rows: [
          for (var r = 0; r < rows.length; r++)
            DataRow(cells: [
              for (var c = 0; c < columns.length; c++)
                DataCell(_Cell(
                  value: c < rows[r].length ? rows[r][c] : '',
                  error: errors['$r:${columns[c]}'],
                  onChanged: (v) => onCellChanged(r, c, v),
                )),
              DataCell(IconButton(
                tooltip: 'Bu satırı çıkar',
                icon: const Icon(Icons.close, size: 16),
                onPressed: () => onRowRemoved(r),
              )),
            ]),
        ],
      ),
    );
  }
}

// Tek hücre. Düzenlenebilir olması şart: modelin hatasını gören kullanıcı, belgeyi
// yeniden yüklemek yerine hücreyi düzeltebilmeli.
class _Cell extends StatefulWidget {
  final String value;
  final DocumentCellError? error;
  final ValueChanged<String> onChanged;

  const _Cell({required this.value, this.error, required this.onChanged});

  @override
  State<_Cell> createState() => _CellState();
}

class _CellState extends State<_Cell> {
  late final TextEditingController _controller =
      TextEditingController(text: widget.value);

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final error = widget.error;

    return SizedBox(
      width: 150,
      child: TextField(
        controller: _controller,
        onChanged: widget.onChanged,
        style: const TextStyle(fontSize: 12.5),
        decoration: InputDecoration(
          isDense: true,
          contentPadding: const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
          // Uymayan hücre kırmızı çerçeveyle ve beklenen tiple işaretleniyor:
          // "hatalı" demek yetmez, kullanıcı NE yazması gerektiğini bilmeli.
          errorText: error == null ? null : '${error.expectedType} bekleniyor',
          errorStyle: const TextStyle(fontSize: 10),
          enabledBorder: error == null
              ? null
              : const OutlineInputBorder(
                  borderSide: BorderSide(color: AppColors.danger)),
        ),
      ),
    );
  }
}

// --- küçük parçalar ---------------------------------------------------------------------

class _Banner extends StatelessWidget {
  final IconData icon;
  final Color color;
  final String title;
  final String message;

  const _Banner({
    required this.icon,
    required this.color,
    required this.title,
    required this.message,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.10),
        border: Border.all(color: color.withValues(alpha: 0.45)),
        borderRadius: BorderRadius.circular(AppRadius.control),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, size: 18, color: color),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title,
                    style: TextStyle(
                        fontSize: 13, fontWeight: FontWeight.w600, color: color)),
                const SizedBox(height: 2),
                Text(message,
                    style: const TextStyle(
                        fontSize: 12.5, color: AppColors.muted, height: 1.4)),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _ErrorBanner extends StatelessWidget {
  final String message;
  final VoidCallback onClose;

  const _ErrorBanner({required this.message, required this.onClose});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppColors.danger.withValues(alpha: 0.10),
        border: Border.all(color: AppColors.danger.withValues(alpha: 0.45)),
        borderRadius: BorderRadius.circular(AppRadius.control),
      ),
      child: Row(
        children: [
          const Icon(Icons.error_outline, size: 18, color: AppColors.danger),
          const SizedBox(width: 10),
          Expanded(
            child: Text(message, style: const TextStyle(fontSize: 12.5)),
          ),
          IconButton(
            icon: const Icon(Icons.close, size: 16),
            onPressed: onClose,
          ),
        ],
      ),
    );
  }
}
