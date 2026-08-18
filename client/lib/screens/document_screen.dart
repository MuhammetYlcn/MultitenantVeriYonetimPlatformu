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

/// Bir belge kolonunun kaydederken ne olacağı.
///
/// `undecided` bilinçli olarak var ve varsayılan o: eşleşmeyen bir kolon için hangi
/// varsayılanı seçersek seçelim yanılırız. Ölçülen belgede eşleşmeyen üç kolondan ikisi
/// çöptü ("logo", "web_sitesi"), biri ise verinin kendisiydi ("ürün / hizmet"). Otomatik
/// atmak veriyi kaybettirir, otomatik eklemek seti çöple doldurur — bu yüzden karar
/// kullanıcıya bırakılıyor ve karar verilmeden kaydedilemiyor.
enum ColumnAction { undecided, map, addNew, skip }

/// Tek bir belge kolonunun akıbeti.
class _ColumnPlan {
  /// Belgedeki başlık. Kullanıcı düzeltse bile bu değişmez — eşleme buna dayanıyor.
  final String source;

  ColumnAction action;

  /// `map` ise hedef setteki kolon adı.
  String? target;

  /// `addNew` ise KULLANICININ yazdığı kolon adı.
  ///
  /// Belgedeki başlık kullanılmıyor. O adı model uyduruyor ("ürün / hizmet", "logo") ve
  /// yeni kolon sete kalıcı olarak yazılıyor — panoda, sorguda, dosya dışa aktarımında hep
  /// o ad görünecek. Kolonu açan da adını koyan da müşteri olmalı.
  String? newName;

  /// Adlar tutuyor ama tipler tutmuyor (belgede metin, sette tarih gibi).
  bool typeConflict;

  // newName kurucuda alınmıyor: adı her zaman kullanıcı, kolon "yeni kolon" yapıldığı anda
  // giriyor (bkz. _askColumnName). Kurucuda hazır bir ad geçebilmek, o adı sessizce koyan
  // bir yol açardı.
  _ColumnPlan({
    required this.source,
    this.action = ColumnAction.undecided,
    this.target,
    this.typeConflict = false,
  });

  /// Kaydedilecek kolon adı: eşlendiyse hedefin adı, yeni kolonsa kullanıcının yazdığı ad.
  String? get savedName => switch (action) {
        ColumnAction.map => target,
        ColumnAction.addNew => newName,
        _ => null,
      };
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

  /// Hedef setin kolonlarıyla kurulan eşleme. Hedef değişince yeniden alınıyor.
  DocumentAlignment? _alignment;

  /// Kolon başına karar (eşle / yeni kolon / kaydetme). `_columns` ile aynı sırada.
  List<_ColumnPlan> _plans = [];

  /// Hedef seçicinin listesi. Kullanıcı önerilenlerle sınırlı değil: belge yanlış sete
  /// yollanmak üzereyken düzeltebilmeli.
  List<Dataset> _datasets = const [];

  /// Hedef olarak "yeni set" seçildiyse kullanıcının verdiği ad.
  String? _newDatasetName;

  /// Hedef değişti, eşleme sunucudan geliyor.
  bool _aligning = false;

  /// Kolon kararları her değiştiğinde artan sayaç.
  ///
  /// Başlıktaki seçicilerin anahtarına giriyor: kullanıcı "yeni kolon" deyip ad kutusunda
  /// vazgeçtiğinde seçici kendi içinde çoktan değişmiş oluyor, sayaç onu yeniden kurup
  /// gerçek karara döndürüyor.
  int _planRevision = 0;

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

      // Setlerin listesi hedef seçici için gerekiyor; hedef setin adı da buradan geliyor.
      // Ad işin kendisinde taşınmıyor (kimliği taşıyor) ve set adı sonradan değişebilir.
      var datasets = const <Dataset>[];
      try {
        datasets = await ApiService.getDatasets();
      } catch (_) {
        // Liste alınamazsa akış durmaz: tablo yine gösterilir, yalnız hedef değiştirilemez.
      }

      final targetName = datasets
          .where((d) => d.id == job.datasetId)
          .map((d) => d.name)
          .firstOrNull;

      if (!mounted) return;
      setState(() {
        _result = extraction;
        _fileName = _fileName ?? job.fileName;
        _targetId = job.datasetId;
        _targetName = targetName;
        _datasets = datasets;
        _imageBytes = image;
        _alreadyConfirmed = job.isConfirmed;
        _columns = List.of(extraction.columns);
        _rows = extraction.rows.map(List<String>.of).toList();
        _applyAlignment(extraction.alignment);
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

  /// Sunucudan gelen eşlemeyi kolon KARARLARINA çevirir.
  ///
  /// Eşleşen kolon doğrudan hedefine bağlanıyor (kullanıcı isterse değiştirir), eşleşmeyen
  /// ise kararsız bırakılıyor. setState İÇİNDEN çağrılıyor — kendisi setState çağırmaz.
  void _applyAlignment(DocumentAlignment? alignment) {
    _alignment = alignment;

    final byDiscovered = alignment?.byDiscovered ?? const <String, ColumnMapping>{};

    _plans = _columns.map((c) {
      final mapping = byDiscovered[c];
      if (mapping == null) return _ColumnPlan(source: c);

      return _ColumnPlan(
        source: c,
        action: ColumnAction.map,
        target: mapping.target,
        typeConflict: mapping.typeConflict,
      );
    }).toList();
  }

  /// Hedef veri setini değiştirir ve eşlemeyi YENİDEN kurar — belge tekrar okunmadan.
  Future<void> _selectTarget(String datasetId) async {
    setState(() {
      _aligning = true;
      _error = null;
    });

    try {
      final alignment = await ApiService.alignDocument(datasetId, _columns, _rows);
      if (!mounted) return;

      setState(() {
        _targetId = datasetId;
        _targetName = alignment.name;
        _newDatasetName = null;
        _applyAlignment(alignment);
        _aligning = false;
        _planRevision++;
      });
    } catch (e) {
      if (!mounted) return;
      // Hizalama alınamadıysa hedef değişmedi; seçici de eski hedefe dönmeli.
      setState(() {
        _error = e.toString();
        _aligning = false;
        _planRevision++;
      });
    }
  }

  /// Hedefi "yeni veri seti"ne çevirir. Adı kullanıcı veriyor: belgeden çıkan öneri
  /// ("Fatura") çoğu zaman yeterli değil, aynı adla ikinci bir set açılması işi karıştırır.
  Future<void> _selectNewDataset() async {
    final controller = TextEditingController(
        text: _newDatasetName ?? _result?.suggestedName ?? 'Belgeden gelen veriler');

    final name = await showDialog<String>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Yeni veri seti'),
        content: TextField(
          controller: controller,
          autofocus: true,
          decoration: const InputDecoration(labelText: 'Veri seti adı'),
          onSubmitted: (v) => Navigator.of(ctx).pop(v.trim()),
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(ctx).pop(), child: const Text('Vazgeç')),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(controller.text.trim()),
            child: const Text('Tamam'),
          ),
        ],
      ),
    );

    controller.dispose();
    if (!mounted) return;

    // Vazgeçildi ya da ad boş bırakıldı: hedef DEĞİŞMEZ. Sayaç seçiciyi yeniden kurup
    // gerçek hedefe döndürüyor — yoksa liste "yeni veri seti" yazılı kalır ve kullanıcı
    // adını koymadığı bir set açtığını sanır.
    if (name == null || name.isEmpty) {
      setState(() => _planRevision++);
      return;
    }

    setState(() {
      _targetId = null;
      _targetName = null;
      _newDatasetName = name;
      // Şema yok: kolonlar olduğu gibi yeni sete gidiyor, eşlenecek bir şey kalmıyor.
      _applyAlignment(null);
      _planRevision++;
    });
  }

  /// Sette olup belgede çıkmayan bir kolonu tabloya BOŞ olarak ekler.
  ///
  /// Model bir alanı hiç okumamış olabilir (silik yazı, kırpılmış kenar). O kolon eskiden
  /// kaydedilirken sessizce boş kalıyordu; artık kullanıcı ekleyip elle doldurabiliyor.
  void _addMissingColumn(String name) {
    setState(() {
      _columns.add(name);
      for (final row in _rows) {
        row.add('');
      }
      _plans.add(_ColumnPlan(source: name, action: ColumnAction.map, target: name));
      _planRevision++;
    });
  }

  /// Kolon kararı değişti.
  ///
  /// "Yeni kolon" seçilince ADI SORULUYOR: sete kalıcı olarak yazılacak kolonun adını
  /// belgeden okuyan model değil, kullanıcı koyar. Vazgeçilirse kolonun kararı olduğu
  /// gibi kalıyor (`_planRevision` seçiciyi eski değerine geri çeviriyor).
  Future<void> _setPlan(int index, ColumnAction action, {String? target}) async {
    String? newName;

    if (action == ColumnAction.addNew) {
      newName = await _askColumnName(_plans[index]);
      if (!mounted) return;

      if (newName == null) {
        setState(() => _planRevision++);
        return;
      }
    }

    setState(() {
      final plan = _plans[index];
      plan.action = action;
      plan.target = target;
      plan.newName = newName;
      // Tip uyarısı yalnız sunucunun kurduğu eşleme için geçerliydi; kullanıcı hedefi
      // değiştirdiyse uyarıyı taşımak yanlış yere işaret etmek olurdu.
      plan.typeConflict = false;
      _planRevision++;
    });
  }

  /// Yeni kolonun adını sorar. Vazgeçilirse ya da boş bırakılırsa null döner.
  ///
  /// Kutu BOŞ açılıyor, belgedeki başlıkla dolu değil: hazır gelen bir ad, kullanıcının
  /// onaylayıp geçmesine yol açar ve kolonu yine model adlandırmış olurdu. Başlık yalnız
  /// hatırlatma olarak yazıyor.
  Future<String?> _askColumnName(_ColumnPlan plan) async {
    final controller = TextEditingController(text: plan.newName ?? '');

    final name = await showDialog<String>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Yeni kolon'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Belgedeki başlık: "${plan.source}"',
                style: const TextStyle(fontSize: 12.5, color: AppColors.muted)),
            const SizedBox(height: 12),
            TextField(
              controller: controller,
              autofocus: true,
              decoration: const InputDecoration(
                labelText: 'Veri setine eklenecek kolon adı',
                hintText: 'ör. birim',
              ),
              onSubmitted: (v) => Navigator.of(ctx).pop(v.trim()),
            ),
          ],
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(ctx).pop(), child: const Text('Vazgeç')),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(controller.text.trim()),
            child: const Text('Ekle'),
          ),
        ],
      ),
    );

    controller.dispose();

    return (name == null || name.isEmpty) ? null : name;
  }

  /// Kaydedilecek tablo: kararlara göre süzülmüş kolonlar, hedefteki adlarıyla.
  ({List<String> columns, List<List<String>> rows, List<String> newColumns})
      get _plannedTable {
    final indexes = <int>[];
    final columns = <String>[];
    final newColumns = <String>[];

    for (var i = 0; i < _plans.length; i++) {
      final plan = _plans[i];

      // Hedef yoksa (yeni set) eşlenecek şema da yok: atılmayan her kolon olduğu gibi gider.
      final name = _targetId == null
          ? (plan.action == ColumnAction.skip ? null : plan.source)
          : plan.savedName;

      if (name == null) continue;

      indexes.add(i);
      columns.add(name);
      if (_targetId != null && plan.action == ColumnAction.addNew) newColumns.add(name);
    }

    final rows = _rows
        .map((row) => [for (final i in indexes) i < row.length ? row[i] : ''])
        .toList();

    return (columns: columns, rows: rows, newColumns: newColumns);
  }

  /// Karar verilmemiş kolonlar. Bunlar dururken kaydetmeye izin YOK: sessiz kaybı
  /// kapatmanın tek yolu, kullanıcıyı her kolon için bir şey seçmeye zorlamak.
  List<String> get _undecided => _targetId == null
      ? const []
      : _plans
          .where((p) => p.action == ColumnAction.undecided)
          .map((p) => p.source)
          .toList();

  /// Aynı set kolonuna bağlanmış birden fazla belge kolonu.
  List<String> get _conflicting {
    final counts = <String, int>{};
    for (final plan in _plans) {
      final name = plan.savedName;
      if (name != null) counts[name] = (counts[name] ?? 0) + 1;
    }

    return counts.entries.where((e) => e.value > 1).map((e) => e.key).toList();
  }

  /// Kaydetmeyi engelleyen sebep — yoksa null.
  String? get _blockingReason {
    if (_rows.isEmpty) return 'Kaydedilecek satır kalmadı.';

    if (_undecided.isNotEmpty) {
      return '${_undecided.length} kolon için karar verilmedi: '
          '${_undecided.join(", ")}. Her birini eşleyin, ekleyin ya da "Kaydetme" deyin.';
    }

    if (_conflicting.isNotEmpty) {
      return 'Aynı kolona birden fazla eşleme var: ${_conflicting.join(", ")}.';
    }

    // Sette zaten bulunan bir adla yeni kolon açmak, aynı adı taşıyan iki kolon bırakır.
    // Kullanıcının istediği neredeyse her zaman EŞLEMEKTİR; sunucu da bunu reddediyor,
    // ama hatayı kaydetmeye basmadan önce söylemek gerekiyor.
    final existing = (_alignment?.targetColumns ?? const []).map((c) => c.name).toSet();
    final clashing = _plans
        .where((p) => p.action == ColumnAction.addNew && existing.contains(p.newName))
        .map((p) => p.newName!)
        .toList();

    if (clashing.isNotEmpty) {
      return '${clashing.join(", ")} adında bir kolon sette zaten var; '
          'yeni kolon açmak yerine o kolona eşleyin.';
    }

    if (_plannedTable.columns.isEmpty) return 'Kaydedilecek kolon kalmadı.';

    return null;
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final target = _targetId;
      final planned = _plannedTable;
      final int saved;
      String? datasetName;

      if (target != null) {
        // jobId gönderiliyor: sunucu satırları yazdıktan sonra belge görüntüsünü siliyor
        // ve işi onaylanmış olarak damgalıyor (aynı belge ikinci kez kaydedilemez).
        saved = await ApiService.confirmDocument(target, planned.columns, planned.rows,
            jobId: widget.jobId, newColumns: planned.newColumns);
      } else {
        // Yeni set: tabloyu CSV'ye çevirip VAR OLAN yükleme yolundan geçiriyoruz.
        // Böylece şema algılama ve satır doğrulama, dosyadan gelen veriyle birebir
        // aynı koddan geçiyor — belgeye özel ikinci bir içe aktarma yolu doğmuyor.
        datasetName = _newDatasetName ??
            (_result?.suggestedName.isNotEmpty == true
                ? _result!.suggestedName
                : 'Belgeden gelen veriler');
        await ApiService.uploadDataset(
          name: datasetName,
          bytes: utf8.encode(_toCsv(planned.columns, planned.rows)),
          filename: 'belge.csv',
        );
        saved = planned.rows.length;
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
  static String _toCsv(List<String> columns, List<List<String>> rows) {
    String cell(String v) => '"${v.replaceAll('"', '""')}"';
    final buffer = StringBuffer()..writeln(columns.map(cell).join(','));
    for (final row in rows) {
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
        plans: _plans,
        planRevision: _planRevision,
        alignment: _alignment,
        datasets: _datasets,
        targetId: _targetId,
        targetName: _targetName,
        newDatasetName: _newDatasetName,
        saving: _saving,
        aligning: _aligning,
        alreadyConfirmed: _alreadyConfirmed,
        blockingReason: _blockingReason,
        onCellChanged: (r, c, v) => setState(() => _rows[r][c] = v),
        onRowRemoved: (r) => setState(() => _rows.removeAt(r)),
        onPickSuggestion: _reReadWithSchema,
        onSelectTarget: _selectTarget,
        onSelectNewDataset: _selectNewDataset,
        onPlanChanged: _setPlan,
        onAddMissingColumn: _addMissingColumn,
        onSave: _blockingReason == null ? _save : null,
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
  final List<_ColumnPlan> plans;

  /// Kararlar değiştikçe artan sayaç — başlıktaki seçicilerin anahtarına giriyor.
  final int planRevision;

  final DocumentAlignment? alignment;
  final List<Dataset> datasets;
  final String? targetId;
  final String? targetName;
  final String? newDatasetName;
  final bool saving;
  final bool aligning;
  final bool alreadyConfirmed;

  /// Kaydetmeyi engelleyen sebep (karar verilmemiş kolon, çakışan eşleme…) — yoksa null.
  final String? blockingReason;

  final void Function(int row, int col, String value) onCellChanged;
  final void Function(int row) onRowRemoved;
  final void Function(String datasetId) onPickSuggestion;
  final void Function(String datasetId) onSelectTarget;
  final VoidCallback onSelectNewDataset;
  final void Function(int index, ColumnAction action, {String? target}) onPlanChanged;
  final void Function(String name) onAddMissingColumn;
  final VoidCallback? onSave;
  final VoidCallback onCancel;
  final VoidCallback onDiscard;

  const _ReviewPanel({
    required this.result,
    required this.columns,
    required this.rows,
    required this.plans,
    required this.planRevision,
    required this.alignment,
    required this.datasets,
    required this.targetId,
    required this.targetName,
    required this.newDatasetName,
    required this.saving,
    required this.aligning,
    required this.blockingReason,
    required this.onCellChanged,
    required this.onRowRemoved,
    required this.onPickSuggestion,
    required this.onSelectTarget,
    required this.onSelectNewDataset,
    required this.onPlanChanged,
    required this.onAddMissingColumn,
    required this.onSave,
    this.alreadyConfirmed = false,
    required this.onCancel,
    required this.onDiscard,
  });

  /// Sette var ama tabloda karşılığı olmayan kolonlar — kullanıcı elle ekleyip doldurabilir.
  List<String> get _missing {
    final used = plans
        .map((p) => p.savedName)
        .whereType<String>()
        .toSet();

    return (alignment?.targetColumns ?? const [])
        .map((c) => c.name)
        .where((name) => !used.contains(name))
        .toList();
  }

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

        // Hedef HER ZAMAN sorulur — eşleşme bulunmuş olsa bile.
        //
        // Sistem seti tahmin ediyor, bilmiyor. Yanlış sete yazılmış bir belge hata vermez,
        // sessizce yanlış toplam üretir; sormanın bedeli ise tek bir satırlık ekran alanı.
        const SizedBox(height: 4),
        _TargetCard(
          key: ValueKey('hedef|$planRevision'),
          datasets: datasets,
          targetId: targetId,
          targetName: targetName,
          newDatasetName: newDatasetName,
          aligning: aligning,
          onSelect: onSelectTarget,
          onSelectNew: onSelectNewDataset,
        ),

        if (targetId == null) ...[
          const SizedBox(height: 12),
          _Suggestions(result: result, onPick: onPickSuggestion),
        ],

        if (targetId != null && _missing.isNotEmpty) ...[
          const SizedBox(height: 12),
          _MissingColumns(names: _missing, onAdd: onAddMissingColumn),
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
              // Başlıklar artık düz metin değil: her kolonun nereye yazılacağı buradan
              // seçiliyor. Eskiden kullanıcı yalnız HÜCREYİ düzeltebiliyordu, kolonun
              // tamamının düştüğünü ise göremiyordu bile.
              if (targetId != null)
                Padding(
                  padding: const EdgeInsets.only(bottom: 12),
                  child: Text(
                    'Her kolonun ${targetName ?? "veri setinde"} nereye yazılacağını '
                    'başlıklardan seçebilirsin.',
                    style: const TextStyle(fontSize: 12.5, color: AppColors.muted),
                  ),
                ),
              _EditableTable(
                columns: columns,
                rows: rows,
                errors: errors,
                plans: plans,
                planRevision: planRevision,
                targetColumns: targetId == null
                    ? const []
                    : (alignment?.targetColumns ?? const []).map((c) => c.name).toList(),
                mappingEnabled: targetId != null && !alreadyConfirmed,
                onCellChanged: onCellChanged,
                onRowRemoved: onRowRemoved,
                onPlanChanged: onPlanChanged,
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),
        // Kaydetmenin neden kapalı olduğu SÖYLENİYOR: sebebi yazmadan düğmeyi soluk
        // bırakmak, kullanıcıyı ekranda ne yapacağını arayarak dolaştırırdı.
        if (!alreadyConfirmed && blockingReason != null) ...[
          _Banner(
            icon: Icons.rule,
            color: AppColors.warning,
            title: 'Kaydetmeden önce',
            message: blockingReason!,
          ),
          const SizedBox(height: 4),
        ],
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

// --- hedef veri seti --------------------------------------------------------------------

/// "Bu belge hangi sete yazılacak?" — eşleşme bulunmuş olsa bile sorulan soru.
///
/// Sistemin seti tahmin etmesi ile bilmesi aynı şey değil. Yanlış sete yazılan bir belge
/// hiçbir hata üretmez, yalnız o setin toplamlarını sessizce bozar. Bu yüzden hedef bir
/// varsayım değil, ekranda görünen ve değiştirilebilen bir seçim.
class _TargetCard extends StatelessWidget {
  static const _newValue = '__new__';

  final List<Dataset> datasets;
  final String? targetId;
  final String? targetName;
  final String? newDatasetName;
  final bool aligning;
  final void Function(String datasetId) onSelect;
  final VoidCallback onSelectNew;

  const _TargetCard({
    super.key,
    required this.datasets,
    required this.targetId,
    required this.targetName,
    required this.newDatasetName,
    required this.aligning,
    required this.onSelect,
    required this.onSelectNew,
  });

  @override
  Widget build(BuildContext context) {
    // Hedef listede yoksa (liste alınamadı ya da set yeni açıldı) seçenek elle eklenir:
    // aksi hâlde açılır listenin değeri karşılıksız kalır.
    final items = <DropdownMenuItem<String>>[
      for (final d in datasets)
        DropdownMenuItem(value: d.id, child: Text(d.name, overflow: TextOverflow.ellipsis)),
      if (targetId != null && !datasets.any((d) => d.id == targetId))
        DropdownMenuItem(value: targetId, child: Text(targetName ?? 'Seçili set')),
      DropdownMenuItem(
        value: _newValue,
        child: Text(newDatasetName == null
            ? 'Yeni veri seti oluştur…'
            : 'Yeni set: $newDatasetName'),
      ),
    ];

    return SectionCard(
      title: 'Hedef veri seti',
      subtitle: targetId == null
          ? 'Satırlar "${newDatasetName ?? "Belgeden gelen veriler"}" adıyla açılacak '
              'yeni bir sete yazılacak.'
          : 'Doğru set mi? Değilse buradan değiştir — belge yeniden okunmaz, '
              'kolonlar yeni şemaya göre eşlenir.',
      child: Row(
        children: [
          Expanded(
            child: DropdownButtonFormField<String>(
              initialValue: targetId ?? _newValue,
              isExpanded: true,
              decoration: const InputDecoration(isDense: true),
              items: items,
              onChanged: aligning
                  ? null
                  : (value) {
                      if (value == null) return;
                      if (value == _newValue) {
                        onSelectNew();
                      } else if (value != targetId) {
                        onSelect(value);
                      }
                    },
            ),
          ),
          if (aligning) ...[
            const SizedBox(width: 12),
            const ButtonSpinner(),
          ],
        ],
      ),
    );
  }
}

// --- sette olup belgede çıkmayan kolonlar -----------------------------------------------

/// Hedefte bulunan ama belgede karşılığı çıkmayan kolonlar.
///
/// Eskiden bu kolonlar sessizce boş kaydediliyordu. Model bir alanı hiç okumamış olabilir
/// (silik yazı, kırpılmış kenar) — kullanıcı belgeye bakarak değeri görüyorsa kolonu
/// ekleyip elle doldurabilmeli.
class _MissingColumns extends StatelessWidget {
  final List<String> names;
  final void Function(String name) onAdd;

  const _MissingColumns({required this.names, required this.onAdd});

  @override
  Widget build(BuildContext context) {
    return SectionCard(
      title: 'Sette olup belgede çıkmayan kolonlar',
      subtitle: 'Bu alanlar boş kaydedilecek. Ekleyip elle doldurabilirsin.',
      child: Wrap(
        spacing: 8,
        runSpacing: 8,
        children: [
          for (final name in names)
            ActionChip(
              avatar: const Icon(Icons.add, size: 16),
              label: Text(name),
              onPressed: () => onAdd(name),
            ),
        ],
      ),
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
  final List<_ColumnPlan> plans;
  final int planRevision;

  /// Hedef setin kolon adları — başlıktaki eşleme seçeneklerinin listesi.
  final List<String> targetColumns;

  /// Hedef yoksa (yeni set) ya da belge kaydedilmişse başlıklar düz metin kalır.
  final bool mappingEnabled;

  final void Function(int row, int col, String value) onCellChanged;
  final void Function(int row) onRowRemoved;
  final void Function(int index, ColumnAction action, {String? target}) onPlanChanged;

  const _EditableTable({
    required this.columns,
    required this.rows,
    required this.errors,
    required this.plans,
    required this.planRevision,
    required this.targetColumns,
    required this.mappingEnabled,
    required this.onCellChanged,
    required this.onRowRemoved,
    required this.onPlanChanged,
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

    // Başka bir kolona bağlanmış hedefler listeden düşürülüyor: aynı alana iki kolon
    // eşlenirse biri diğerinin üstüne yazar ve hangisinin kaldığı rastlantıya kalır.
    final used = plans.map((p) => p.target).whereType<String>().toSet();

    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      child: DataTable(
        columnSpacing: 18,
        headingRowHeight: mappingEnabled ? 78 : 38,
        dataRowMinHeight: 44,
        dataRowMaxHeight: 52,
        columns: [
          for (var c = 0; c < columns.length; c++)
            DataColumn(
              label: mappingEnabled && c < plans.length
                  ? _ColumnHeader(
                      key: ValueKey('$c|$planRevision'),
                      plan: plans[c],
                      options: targetColumns
                          .where((t) => !used.contains(t) || plans[c].target == t)
                          .toList(),
                      onChanged: (action, {target}) =>
                          onPlanChanged(c, action, target: target),
                    )
                  : Text(columns[c],
                      style: const TextStyle(
                          fontSize: 12.5, fontWeight: FontWeight.w600)),
            ),
          const DataColumn(label: Text('')),
        ],
        rows: [
          for (var r = 0; r < rows.length; r++)
            DataRow(cells: [
              for (var c = 0; c < columns.length; c++)
                DataCell(_Cell(
                  value: c < rows[r].length ? rows[r][c] : '',
                  // Hata kaydı sunucudaki ŞEMA adıyla geliyor; kolon başka bir alana
                  // eşlendiyse işaret de oraya taşınmalı.
                  error: errors['$r:${_errorKey(c)}'],
                  faded: c < plans.length && plans[c].action == ColumnAction.skip,
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

  String _errorKey(int column) => column < plans.length
      ? (plans[column].savedName ?? plans[column].source)
      : columns[column];
}

/// Tablo başlığı: belgedeki kolon adı + o kolonun nereye yazılacağı.
///
/// Eskiden burası düz metindi. Kullanıcı hücreyi düzeltebiliyor ama kolonun tamamının
/// kaydedilmediğini göremiyordu — ekran "gördüğün kaydedilecek" izlenimi veriyor, oysa
/// şemada karşılığı olmayan kolon sessizce düşüyordu.
class _ColumnHeader extends StatelessWidget {
  static const _addNew = '__new__';
  static const _skip = '__skip__';

  final _ColumnPlan plan;

  /// Seçilebilir hedef kolonlar (başkasına bağlanmış olanlar hariç).
  final List<String> options;

  final void Function(ColumnAction action, {String? target}) onChanged;

  const _ColumnHeader({
    super.key,
    required this.plan,
    required this.options,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    final value = switch (plan.action) {
      ColumnAction.map => plan.target,
      ColumnAction.addNew => _addNew,
      ColumnAction.skip => _skip,
      ColumnAction.undecided => null,
    };

    // Karar verilmemiş kolon dikkat çekmeli: kaydetmeyi engelleyen şey bu.
    final color = switch (plan.action) {
      ColumnAction.undecided => AppColors.warning,
      ColumnAction.skip => AppColors.muted,
      _ => AppColors.border,
    };

    return SizedBox(
      width: 150,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  plan.source,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600),
                ),
              ),
              // Tip uyuşmazlığı eşlemeyi bozmuyor ama kaydetmede hücre hatası olarak
              // dönebilir; kullanıcı nedenini başlıkta görsün.
              if (plan.typeConflict)
                const Tooltip(
                  message: 'Bu kolonun tipi sette farklı; değerler uymayabilir.',
                  child: Icon(Icons.warning_amber_rounded,
                      size: 14, color: AppColors.warning),
                ),
            ],
          ),
          const SizedBox(height: 2),
          SizedBox(
            height: 34,
            child: DropdownButtonFormField<String>(
              initialValue: value,
              isExpanded: true,
              isDense: true,
              hint: const Text('Seç…',
                  style: TextStyle(fontSize: 11.5, color: AppColors.warning)),
              style: const TextStyle(fontSize: 11.5, color: AppColors.text),
              decoration: InputDecoration(
                isDense: true,
                contentPadding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
                enabledBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(AppRadius.control),
                  borderSide: BorderSide(color: color),
                ),
              ),
              items: [
                for (final option in options)
                  DropdownMenuItem(
                      value: option,
                      child: Text(option, overflow: TextOverflow.ellipsis)),
                // Ad seçildikten sonra seçicide o ad yazıyor: kullanıcı hangi kolonun
                // hangi adla açılacağını tabloya bakarak görebilmeli.
                DropdownMenuItem(
                  value: _addNew,
                  child: Text(
                    plan.newName == null
                        ? '+ Yeni kolon (adını sen yaz)'
                        : 'Yeni: ${plan.newName}',
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                const DropdownMenuItem(value: _skip, child: Text('Kaydetme')),
              ],
              onChanged: (selected) {
                if (selected == null) return;
                if (selected == _addNew) {
                  onChanged(ColumnAction.addNew);
                } else if (selected == _skip) {
                  onChanged(ColumnAction.skip);
                } else {
                  onChanged(ColumnAction.map, target: selected);
                }
              },
            ),
          ),
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

  /// Kolon "Kaydetme" seçildiyse hücre soluk görünür: değer ekranda duruyor ama
  /// kaydedilmeyeceği bakışta anlaşılmalı.
  final bool faded;

  final ValueChanged<String> onChanged;

  const _Cell({
    required this.value,
    this.error,
    this.faded = false,
    required this.onChanged,
  });

  @override
  State<_Cell> createState() => _CellState();
}

class _CellState extends State<_Cell> {
  late final TextEditingController _controller =
      TextEditingController(text: widget.value);

  // Hücreler tabloda KONUMA göre yaşıyor: bir satır çıkarılınca aynı konuma alttaki satır
  // geliyor ama denetleyicideki metin eski satırınki kalıyordu. Kaydedilen veri doğruydu
  // (o `_rows`'tan gidiyor), ekranda görünen yanlıştı — onay ekranında bu, kullanıcının
  // yanlış tabloya bakarak onaylaması demek.
  @override
  void didUpdateWidget(_Cell oldWidget) {
    super.didUpdateWidget(oldWidget);

    // Kullanıcı yazarken imleci kaçırmamak için yalnız dışarıdan gelen değişiklik uygulanır.
    if (widget.value != oldWidget.value && widget.value != _controller.text) {
      _controller.text = widget.value;
    }
  }

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
        style: TextStyle(
          fontSize: 12.5,
          color: widget.faded ? AppColors.muted : null,
          decoration: widget.faded ? TextDecoration.lineThrough : null,
        ),
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
