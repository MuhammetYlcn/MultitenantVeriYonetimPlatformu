import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../api_service.dart';
import '../theme/app_theme.dart';
import '../widgets/ui.dart';

// Belgeden veri girişinin ONAY adımı — sohbet ekranının üzerinde açılan katman.
//
// Ekranın varlık sebebi tek cümleyle: model %100 doğru okumuyor, o yüzden okuduğu şey
// doğrudan kaydedilmiyor. Kullanıcı belgeyi ve çıkarımı YAN YANA görüp düzeltiyor,
// kaydetme kararını o veriyor. Ölçümde alan doğruluğu 8/8'e kadar çıktı ama kalem
// tablosunda %94'te kaldı; kalan %6 gözle yakalanmazsa sessizce veriye karışır.
//
// Neden ayrı bir menü öğesi değil de katman: belge yükleme artık sohbetteki ataç
// düğmesinden başlıyor, çünkü insanlar bir konuşma alanı görünce belgeyi oraya bırakıyor.
// Ama ONAYLAMA sohbete gömülemez — dikkat isteyen, geniş alan isteyen, yavaş bir iştir;
// sohbet balonuna sıkıştırılsa hem sıkışık görünür hem de akış kaydıkça gözden kaybolur.
// Bu yüzden giriş sohbette, onay kendi tam genişlikteki yüzeyinde duruyor.

/// Katmanın çağırana döndürdüğü sonuç.
///
/// İki farklı çıkış var: satırlar kaydedildi ya da belge yeni bir işle yeniden okunmaya
/// verildi. Sohbet ekranı buna bakıp akışına ya "N satır eklendi" satırı ya da yeni bir
/// iş kartı düşürüyor.
class DocumentReviewResult {
  final int? savedRows;
  final String? datasetId;
  final String? datasetName;

  /// Keşiften çıkan öneri seçilip belge şemayla YENİDEN okutulduysa yeni işin kimliği.
  final String? requeuedJobId;

  /// Belge atıldıysa true: kart akıştan kaldırılır.
  final bool discarded;

  const DocumentReviewResult.saved({
    required this.savedRows,
    required this.datasetId,
    required this.datasetName,
  })  : requeuedJobId = null,
        discarded = false;

  const DocumentReviewResult.requeued(this.requeuedJobId)
      : savedRows = null,
        datasetId = null,
        datasetName = null,
        discarded = false;

  const DocumentReviewResult.discarded()
      : savedRows = null,
        datasetId = null,
        datasetName = null,
        requeuedJobId = null,
        discarded = true;
}

class DocumentReviewPage extends StatefulWidget {
  final String jobId;

  /// Belge tarayıcıda hâlâ elde ise baytları. Sunucudaki kopya gösterime yetecek boya
  /// indirilmiş olduğundan, şemayla YENİDEN okutma yalnız bu varken yapılabiliyor.
  final Uint8List? localBytes;
  final String? localFileName;

  const DocumentReviewPage({
    super.key,
    required this.jobId,
    this.localBytes,
    this.localFileName,
  });

  @override
  State<DocumentReviewPage> createState() => _DocumentReviewPageState();
}

class _DocumentReviewPageState extends State<DocumentReviewPage> {
  bool _loading = true;
  bool _saving = false;

  DocumentExtraction? _result;
  String? _fileName;

  /// Hedef veri seti. Keşif işinde başta boş: hangi sete yazılacağına bu ekranda,
  /// sistemin önerilerine bakılarak karar veriliyor.
  String? _targetId;

  Uint8List? _imageBytes;
  bool _alreadyConfirmed = false;

  /// Hedef setin adı. Kaydetme sonrası sohbete düşen satırda kullanılıyor: "veri setine
  /// 8 satır eklendi" demek, kullanıcının hangi sete yazdığını hatırlamasını gerektirirdi.
  String? _targetName;

  /// Tablonun DÜZENLENEBİLİR kopyası. Sunucudan gelen satırlar dokunulmadan duruyor;
  /// kullanıcının neyi değiştirdiği böyle görülebiliyor.
  List<String> _columns = [];
  List<List<String>> _rows = [];

  String? _error;

  @override
  void initState() {
    super.initState();
    _imageBytes = widget.localBytes;
    _fileName = widget.localFileName;
    _load();
  }

  Future<void> _load() async {
    try {
      final job = await ApiService.getDocumentJob(widget.jobId);
      if (!mounted) return;

      if (job.status == 'failed') {
        setState(() {
          _error = job.error ?? 'Belge işlenemedi.';
          _loading = false;
        });
        return;
      }

      final extraction = job.extraction;
      if (extraction == null) {
        setState(() {
          _error = 'Bu belgenin okunması henüz tamamlanmadı.';
          _loading = false;
        });
        return;
      }

      // Görüntü elde yoksa (sohbet yenilenmiş ya da iş başka oturumdan geliyor)
      // sunucudan çekiliyor. Onaylanmış işte silinmiş olabilir; o zaman tablo
      // görüntüsüz gösteriliyor.
      var image = _imageBytes;
      image ??= await ApiService.getDocumentJobImage(widget.jobId);

      // Hedef seti belliyse adı da alınıyor: kaydetme sonrası mesajda kullanılacak.
      // Ad, işin kendisinde taşınmıyor (kimliği taşıyor) ve set adı sonradan değişebilir.
      String? targetName;
      if (job.datasetId != null) {
        try {
          final datasets = await ApiService.getDatasets();
          targetName = datasets
              .where((d) => d.id == job.datasetId)
              .map((d) => d.name)
              .firstOrNull;
        } catch (_) {
          // Ad bulunamazsa akış durmaz; mesaj sette adı olmadan yazılır.
        }
      }

      if (!mounted) return;
      setState(() {
        _result = extraction;
        _fileName = _fileName ?? job.fileName;
        _targetId = job.datasetId;
        _targetName = targetName;
        _imageBytes = image;
        _alreadyConfirmed = job.isConfirmed;
        _columns = List.of(extraction.columns);
        _rows = extraction.rows.map(List<String>.of).toList();
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

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final target = _targetId;
      final int saved;
      String? datasetName;

      if (target != null) {
        // jobId gönderiliyor: sunucu satırları yazdıktan sonra belge görüntüsünü siliyor
        // ve işi onaylanmış olarak damgalıyor (aynı belge ikinci kez kaydedilemez).
        saved = await ApiService.confirmDocument(target, _columns, _rows, jobId: widget.jobId);
      } else {
        // Yeni set: tabloyu CSV'ye çevirip VAR OLAN yükleme yolundan geçiriyoruz.
        // Böylece şema algılama ve satır doğrulama, dosyadan gelen veriyle birebir
        // aynı koddan geçiyor — belgeye özel ikinci bir içe aktarma yolu doğmuyor.
        datasetName = _result?.suggestedName ?? 'Belgeden gelen veriler';
        await ApiService.uploadDataset(
          name: datasetName,
          bytes: utf8.encode(_toCsv()),
          filename: 'belge.csv',
        );
        saved = _rows.length;
      }

      if (!mounted) return;
      Navigator.of(context).pop(DocumentReviewResult.saved(
        savedRows: saved,
        datasetId: target,
        datasetName: datasetName ?? _targetName,
      ));
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.toString();
        _saving = false;
      });
    }
  }

  /// Belgeyi ATAR: iş kaydı ve saklanan görüntü silinir.
  ///
  /// Bu yol olmadan yanlış yüklenen bir belge kalıcı olarak "kontrol bekliyor" durumunda
  /// kalıyordu; kullanıcının elinde onu ortadan kaldıracak hiçbir araç yoktu. Kapatmak
  /// (Vazgeç) işi bitirmez, yalnız ekranı kapatır — ikisi farklı şeyler.
  Future<void> _discard() async {
    final onay = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Belge atılsın mı?'),
        content: const Text(
            'Okunan tablo ve belge görüntüsü silinecek. Veri setine yazılmış satırlar '
            'varsa onlar kalır.'),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(ctx).pop(false), child: const Text('Vazgeç')),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            style: FilledButton.styleFrom(backgroundColor: AppColors.danger),
            child: const Text('At'),
          ),
        ],
      ),
    );

    if (onay != true || !mounted) return;

    setState(() => _saving = true);
    try {
      await ApiService.deleteDocumentJob(widget.jobId);
      if (!mounted) return;
      Navigator.of(context).pop(const DocumentReviewResult.discarded());
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.toString();
        _saving = false;
      });
    }
  }

  // Keşiften çıkan öneriye tıklanınca belge o setin ŞEMASIYLA YENİDEN okunur.
  //
  // Neden ikinci bir model çağrısı göze alınıyor: ilk geçişte model adları kendi seçti ve
  // görüntüye başlık şeridi eklenemedi (şerit şemadan üretiliyor). Şema belli olunca ikisi
  // de devreye giriyor; ölçümde kalem doğruluğu %51'den %94'e bu şekilde çıkmıştı.
  Future<void> _reReadWithSchema(String datasetId) async {
    final bytes = widget.localBytes;
    if (bytes == null) {
      // Sunucudaki kopya gösterim için küçültülmüş; onu geri gönderip okutmak, ölçülenden
      // düşük çözünürlükle çalışmak olurdu.
      setState(() => _error =
          'Bu belgeyi şemaya göre yeniden okutmak için dosyayı tekrar yüklemen gerekiyor.');
      return;
    }

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final job = await ApiService.queueExtractDocument(
          datasetId, bytes, widget.localFileName ?? 'belge.jpg');

      if (!mounted) return;
      Navigator.of(context).pop(DocumentReviewResult.requeued(job.id));
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.toString();
        _saving = false;
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

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: AppColors.surface,
      appBar: AppBar(
        title: Text(_fileName ?? 'Belgeyi kontrol et'),
        // Katmandan çıkış her zaman açık: kullanıcı kaydetmeye zorlanmıyor, iş listede
        // duruyor ve sonra dönülebiliyor.
        leading: IconButton(
          onPressed: _saving ? null : () => Navigator.of(context).pop(),
          icon: const Icon(Icons.close),
          tooltip: 'Kapat',
        ),
      ),
      body: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (_error != null) ...[
              _ErrorBanner(message: _error!, onClose: () => setState(() => _error = null)),
              const SizedBox(height: 12),
            ],
            Expanded(child: _body),
          ],
        ),
      ),
    );
  }

  Widget get _body {
    if (_loading) return const LoadingView();
    if (_result == null) return const SizedBox.shrink();

    final result = _result!;

    // Belge ve çıkarım YAN YANA duruyor. Dar ekranda alt alta düşüyor; ama geniş ekranda
    // yan yana olması şart: kullanıcı eksik satırı ancak belgeye bakarak fark eder.
    return LayoutBuilder(builder: (context, constraints) {
      final wide = constraints.maxWidth > 1000;
      final image = _DocumentPreview(bytes: _imageBytes, name: _fileName ?? 'Belge');
      final table = _ReviewPanel(
        result: result,
        columns: _columns,
        rows: _rows,
        targetId: _targetId,
        saving: _saving,
        alreadyConfirmed: _alreadyConfirmed,
        onCellChanged: (r, c, v) => setState(() => _rows[r][c] = v),
        onRowRemoved: (r) => setState(() => _rows.removeAt(r)),
        onPickSuggestion: _reReadWithSchema,
        onSave: _rows.isEmpty ? null : _save,
        onCancel: () => Navigator.of(context).pop(),
        onDiscard: _discard,
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
  /// null olabilir: onaylanmış ya da süresi dolmuş bir işte görüntü artık saklanmıyor.
  final Uint8List? bytes;
  final String name;

  const _DocumentPreview({required this.bytes, required this.name});

  @override
  Widget build(BuildContext context) {
    final data = bytes;

    return SectionCard(
      title: 'Belge',
      subtitle: name,
      child: data == null
          // Görüntünün yokluğu bir hata değil: ara ürün süresi dolunca siliniyor.
          // Yine de sessizce boş bir kutu bırakmak, kullanıcıya yükleme başarısız olmuş
          // gibi görünürdü.
          ? const Padding(
              padding: EdgeInsets.symmetric(vertical: 32),
              child: Column(children: [
                Icon(Icons.image_not_supported_outlined,
                    size: 32, color: AppColors.muted),
                SizedBox(height: 12),
                Text(
                  'Belge görüntüsü artık saklanmıyor.',
                  style: TextStyle(color: AppColors.muted),
                ),
              ]),
            )
          // Expanded ŞART: kart sınırlı yükseklikte duruyor ama görüntü doğal boyutunu
          // almaya çalışıyor. Dikey uzun bir faturada bu, kartın altından taşan bir
          // görüntü demek oluyordu (ölçüldü: 244 piksel taşma).
          : Expanded(
              child: ClipRRect(
                borderRadius: BorderRadius.circular(AppRadius.control),
                // InteractiveViewer: fişin küçük yazısı ekranda okunmuyor; kullanıcı
                // çıkarımı doğrulayabilmek için belgeye yakınlaşabilmeli.
                child: InteractiveViewer(
                  maxScale: 6,
                  child: Image.memory(data, fit: BoxFit.contain),
                ),
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
  final bool alreadyConfirmed;
  final void Function(int row, int col, String value) onCellChanged;
  final void Function(int row) onRowRemoved;
  final void Function(String datasetId) onPickSuggestion;
  final VoidCallback? onSave;
  final VoidCallback onCancel;
  final VoidCallback onDiscard;

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
    this.alreadyConfirmed = false,
    required this.onCancel,
    required this.onDiscard,
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
        // Kaydedilmiş bir belge tekrar açılabiliyor (iş listesi kalıcı). Tabloyu göstermek
        // doğru — kullanıcı ne kaydettiğini görebilmeli — ama düğmeyi açık bırakmak aynı
        // satırları ikinci kez eklerdi.
        if (alreadyConfirmed)
          _Banner(
            icon: Icons.check_circle_outline,
            color: AppColors.muted,
            title: 'Bu belge zaten kaydedildi',
            message: 'Yukarıdaki satırlar veri setine eklenmişti; tekrar kaydedilemez.',
          )
        else
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
              // "Sonra bak" ile "at" farklı şeyler ve ikisi de gerekli: ilki işi olduğu
              // yerde bırakır, ikincisi ortadan kaldırır. Yanlış belge yüklendiğinde
              // yalnız kapatabilmek, kullanıcıyı bitmeyen bir hatırlatmaya mahkûm ederdi.
              TextButton(
                  onPressed: saving ? null : onCancel, child: const Text('Sonra bak')),
              const Spacer(),
              TextButton.icon(
                onPressed: saving ? null : onDiscard,
                icon: const Icon(Icons.delete_outline, size: 18),
                label: const Text('Bu belgeyi at'),
                style: TextButton.styleFrom(foregroundColor: AppColors.danger),
              ),
            ],
          ),
        if (alreadyConfirmed) ...[
          const SizedBox(height: 12),
          Row(
            children: [
              TextButton(onPressed: onCancel, child: const Text('Kapat')),
              const Spacer(),
              // Kaydedilmiş işte de atma açık: satırlar veri setinde kalır, silinen
              // yalnız okuma kaydı ve görüntüsüdür.
              TextButton.icon(
                onPressed: saving ? null : onDiscard,
                icon: const Icon(Icons.delete_outline, size: 18),
                label: const Text('Kaydı sil'),
                style: TextButton.styleFrom(foregroundColor: AppColors.muted),
              ),
            ],
          ),
        ],
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
