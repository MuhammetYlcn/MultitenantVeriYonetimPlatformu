import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../api_service.dart';
import '../job_hub.dart';
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

// Okuma artık İSTEĞİN İÇİNDE değil, arka planda oluyor. Ekranın taşıdığı sonuç bu:
// "reading" adımı bir bekleme değil, bir izleme durumudur — kullanıcı istediği an başka
// ekrana geçebilir, iş sunucuda devam eder ve geri döndüğünde listede bulunur.
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

  /// İzlenen işin kimliği. Kullanıcı ekrandan çıkıp dönerse elimizde kalan tek şey bu.
  String? _jobId;

  /// Son işler — devam edenler ve biten ama henüz onaylanmayanlar.
  List<DocumentJob> _jobs = [];

  /// Onay ekranındaki belge görüntüsü. Yeni yüklemede yereldeki dosyadan, listeden
  /// açılan bir işte sunucudan gelir.
  Uint8List? _imageBytes;

  /// Açık iş daha önce kaydedilmiş mi? İş listesi kalıcı olduğu için kullanıcı onayladığı
  /// bir belgeyi yeniden açabiliyor; ikinci kez kaydetmek aynı satırları tekrar eklerdi.
  bool _alreadyConfirmed = false;

  StreamSubscription<JobNotification>? _hubSub;

  /// Tablonun DÜZENLENEBİLİR kopyası. Sunucudan gelen `_result.rows` dokunulmadan
  /// duruyor; kullanıcının neyi değiştirdiği böyle görülebiliyor.
  List<String> _columns = [];
  List<List<String>> _rows = [];

  String? _error;

  @override
  void initState() {
    super.initState();
    _loadDatasets();
    _loadJobs();

    // Canlı kanal bir KOLAYLIK: kurulamazsa ekran çalışmaya devam eder, kullanıcı
    // durumu elle tazeler. Bu yüzden bağlantı beklenmiyor ve hatası yutuluyor.
    JobHub.connect();
    _hubSub = JobHub.updates.listen(_onJobNotification);
  }

  @override
  void dispose() {
    _hubSub?.cancel();
    super.dispose();
  }

  Future<void> _loadJobs() async {
    try {
      final jobs = await ApiService.listDocumentJobs();
      if (!mounted) return;
      setState(() => _jobs = jobs);
    } catch (_) {
      // İş listesi ekranın çalışması için şart değil; sessizce geçiliyor.
    }
  }

  // Bildirim geldiğinde sonucun kendisi taşınmıyor, yalnız "durum değişti" haberi.
  // İzlenen iş bittiyse tam kayıt çekilir; değilse liste tazelenir.
  void _onJobNotification(JobNotification note) {
    if (!mounted) return;

    if (note.jobId == _jobId && note.isFinished) {
      _openJob(note.jobId);
      return;
    }

    _loadJobs();
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

  Future<void> _pickAndQueue() async {
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
      _imageBytes = file!.bytes;
      _error = null;
    });

    await _queue();
  }

  // Belgeyi KUYRUĞA verir. İstek saniyeler içinde döner; okuma arka planda sürer.
  Future<void> _queue() async {
    final file = _file!;
    try {
      final job = _targetId == null
          ? await ApiService.queueDiscoverDocument(file.bytes, file.name)
          : await ApiService.queueExtractDocument(_targetId!, file.bytes, file.name);

      if (!mounted) return;
      setState(() {
        _jobId = job.id;
        _step = _Step.reading;
      });

      _loadJobs();
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.toString();
        _step = _Step.pick;
      });
    }
  }

  /// Bir işin son durumunu okur; bittiyse onay ekranını açar.
  ///
  /// Hem bildirim geldiğinde hem "yenile" düğmesinde hem de listeden bir işe tıklandığında
  /// çalışan tek yol. Durumun tek kaynağı sunucudaki kayıt.
  Future<void> _openJob(String jobId) async {
    try {
      final job = await ApiService.getDocumentJob(jobId);
      if (!mounted) return;

      if (job.status == 'failed') {
        setState(() {
          _error = job.error ?? 'Belge işlenemedi.';
          _step = _Step.pick;
          _jobId = null;
        });
        _loadJobs();
        return;
      }

      if (!job.isFinished) {
        // Henüz sürüyor: izlemeye devam.
        setState(() {
          _jobId = jobId;
          _step = _Step.reading;
        });
        return;
      }

      final result = job.extraction!;

      // Görüntü elimizde yoksa (kullanıcı listeden eski bir işi açtı) sunucudan çekilir.
      // Onaylanmış işlerde silinmiş olabilir; o zaman tablo görüntüsüz gösterilir.
      var image = _imageBytes;
      if (image == null || _jobId != jobId) {
        image = await ApiService.getDocumentJobImage(jobId);
      }

      if (!mounted) return;
      setState(() {
        _jobId = jobId;
        _targetId = job.datasetId ?? (job.isDiscovery ? null : _targetId);
        _result = result;
        _imageBytes = image;
        _alreadyConfirmed = job.isConfirmed;
        _columns = List.of(result.columns);
        _rows = result.rows.map(List<String>.of).toList();
        _step = _Step.review;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = e.toString());
    }
  }

  // Keşiften çıkan öneriye tıklanınca belge o setin ŞEMASIYLA YENİDEN okunur.
  //
  // Neden ikinci bir model çağrısı göze alınıyor: ilk geçişte model adları kendi seçti ve
  // görüntüye başlık şeridi eklenemedi (şerit şemadan üretiliyor). Şema belli olunca
  // ikisi de devreye giriyor; ölçümde kalem doğruluğu %51'den %94'e bu şekilde çıkmıştı.
  //
  // İkinci okuma da kuyruğa giriyor — birinciden farkı yok, süresi de aynı.
  Future<void> _reReadWithSchema(String datasetId) async {
    if (_file == null) {
      // Görüntü yerelde yok (iş listeden açılmış): şemalı okuma için belge yeniden
      // seçilmeli. Sunucudaki kopya küçültülmüş olduğundan onu geri gönderip ikinci kez
      // okutmak, ölçülenden düşük çözünürlükle çalışmak olurdu.
      setState(() => _error =
          'Bu belgeyi şemaya göre yeniden okutmak için dosyayı tekrar seçmen gerekiyor.');
      return;
    }

    setState(() => _targetId = datasetId);
    await _queue();
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
        // jobId gönderiliyor: sunucu satırları yazdıktan sonra belge görüntüsünü siliyor.
        saved = await ApiService.confirmDocument(target, _columns, _rows, jobId: _jobId);
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

  void _reset() {
    setState(() {
      _step = _Step.pick;
      _file = null;
      _result = null;
      _imageBytes = null;
      _jobId = null;
      _alreadyConfirmed = false;
      _columns = [];
      _rows = [];
      _error = null;
    });
    _loadJobs();
  }

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
        _Step.reading => _waiting,
        _ => _review,
      };

  // ---- ekran: arka planda okunuyor ----

  // Eski hâlinde bu bir kilitli bekleme ekranıydı: istek açık durduğu için kullanıcı
  // hiçbir şey yapamıyordu. Artık iş sunucuda; ekran yalnız izliyor ve bunu açıkça
  // söylüyor — kullanıcının burada beklemek zorunda olmadığını bilmesi gerekiyor.
  Widget get _waiting => SingleChildScrollView(
        child: SectionCard(
          title: 'Belge okunuyor',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Row(children: [
                SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
                SizedBox(width: 12),
                Expanded(
                  child: Text(
                    'Görsel model belgeyi okuyor. Bu işlem belge başına 30-150 saniye '
                    'sürebilir.',
                    style: TextStyle(height: 1.5),
                  ),
                ),
              ]),
              const SizedBox(height: 12),
              const Text(
                'Beklemek zorunda değilsin: işlem sunucuda sürüyor. Başka bir ekrana '
                'geçebilirsin, bittiğinde haber verilecek ve sonuç bu ekranda seni '
                'bekliyor olacak.',
                style: TextStyle(color: AppColors.muted, height: 1.5),
              ),
              const SizedBox(height: 20),
              Row(children: [
                OutlinedButton.icon(
                  onPressed: _jobId == null ? null : () => _openJob(_jobId!),
                  icon: const Icon(Icons.refresh, size: 18),
                  label: const Text('Durumu yenile'),
                ),
                const SizedBox(width: 12),
                TextButton(onPressed: _reset, child: const Text('Listeye dön')),
              ]),
            ],
          ),
        ),
      );

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
              onPressed: _pickAndQueue,
              icon: const Icon(Icons.upload_file, size: 18),
              label: const Text('Belge görüntüsü seç'),
            ),
            const SizedBox(height: 10),
            const Text(
              '.jpg, .png veya .webp · en fazla 15 MB',
              style: TextStyle(fontSize: 12, color: AppColors.muted),
            ),
            if (_jobs.isNotEmpty) ...[
              const SizedBox(height: 28),
              const Divider(),
              const SizedBox(height: 12),
              _JobList(jobs: _jobs, onOpen: _openJob, onRefresh: _loadJobs),
            ],
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
      final image = _DocumentPreview(
        bytes: _imageBytes,
        name: _file?.name ?? _jobs
            .where((j) => j.id == _jobId)
            .map((j) => j.fileName ?? 'Belge')
            .firstOrNull ??
            'Belge',
      );
      final table = _ReviewPanel(
        result: result,
        columns: _columns,
        rows: _rows,
        targetId: _targetId,
        saving: _step == _Step.saving,
        alreadyConfirmed: _alreadyConfirmed,
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
          : ClipRRect(
              borderRadius: BorderRadius.circular(AppRadius.control),
              // InteractiveViewer: fişin küçük yazısı ekranda okunmuyor; kullanıcı çıkarımı
              // doğrulayabilmek için belgeye yakınlaşabilmeli.
              child: InteractiveViewer(
                maxScale: 6,
                child: Image.memory(data, fit: BoxFit.contain),
              ),
            ),
    );
  }
}

// --- son işler ---------------------------------------------------------------------------

// Asenkron akışın istemci tarafındaki karşılığı. Kullanıcı belgeyi yükleyip ekrandan
// çıkabildiği için, geri döndüğünde işini bulabileceği bir yer olmak zorunda.
class _JobList extends StatelessWidget {
  final List<DocumentJob> jobs;
  final void Function(String jobId) onOpen;
  final VoidCallback onRefresh;

  const _JobList({required this.jobs, required this.onOpen, required this.onRefresh});

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(children: [
          const Text('Son belgelerin',
              style: TextStyle(fontWeight: FontWeight.w600, fontSize: 15)),
          const Spacer(),
          IconButton(
            onPressed: onRefresh,
            icon: const Icon(Icons.refresh, size: 18),
            tooltip: 'Yenile',
          ),
        ]),
        const SizedBox(height: 4),
        for (final job in jobs)
          ListTile(
            contentPadding: EdgeInsets.zero,
            leading: Icon(
              switch (job.status) {
                'succeeded' => Icons.check_circle_outline,
                'failed' => Icons.error_outline,
                _ => Icons.hourglass_empty,
              },
              size: 20,
              color: job.status == 'failed' ? AppColors.danger : AppColors.muted,
            ),
            title: Text(job.fileName ?? 'Belge',
                maxLines: 1, overflow: TextOverflow.ellipsis),
            subtitle: Text(
              job.status == 'failed'
                  ? (job.error ?? 'Başarısız')
                  : '${job.statusLabel} · ${job.isDiscovery ? 'keşif' : 'şemalı okuma'}',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(fontSize: 12),
            ),
            // Biten iş açılır, süren iş izlemeye alınır; ikisi de aynı yoldan geçiyor.
            trailing: job.status == 'failed' ? null : const Icon(Icons.chevron_right, size: 18),
            onTap: job.status == 'failed' ? null : () => onOpen(job.id),
          ),
      ],
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
              TextButton(onPressed: saving ? null : onCancel, child: const Text('Vazgeç')),
            ],
          ),
        if (alreadyConfirmed) ...[
          const SizedBox(height: 12),
          TextButton(onPressed: onCancel, child: const Text('Listeye dön')),
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
