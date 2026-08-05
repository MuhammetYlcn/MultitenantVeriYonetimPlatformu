import 'package:flutter/material.dart';
import '../api_service.dart';
import '../theme/app_theme.dart';
import '../platform/platform.dart';
import '../widgets/ui.dart';
import 'home_shell.dart';

// Veri setleri bölümü. Artık kendi Scaffold'unu kurmuyor: kabuğun (AppShell) sağ
// alanına yerleşen bir içerik parçası. Başlık ve eylem butonları PageHeader'da.
class DatasetsPage extends StatefulWidget {
  /// Bir veri setine tıklandığında kabuk onu açar (satır tablosu).
  final void Function(Dataset dataset) onOpen;

  const DatasetsPage({super.key, required this.onOpen});

  @override
  State<DatasetsPage> createState() => _DatasetsPageState();
}

class _DatasetsPageState extends State<DatasetsPage> {
  late Future<List<Dataset>> _future;
  bool _seeding = false;
  bool _uploading = false;

  @override
  void initState() {
    super.initState();
    _future = ApiService.getDatasets();
  }

  // DİKKAT: gövde blok `{}` olmalı. `setState(() => _future = ...)` yazımında ok gövdeli
  // closure atadığı değeri DÖNDÜRÜR; dönen şey bir Future olur ve setState bunu
  // (async işin yanlışlıkla içine konmasını engellemek için) istisna fırlatarak reddeder.
  void _refresh() => setState(() {
        _future = ApiService.getDatasets();
      });

  // Gerçek CSV/Excel yükleme: dosya seç → byte'ları uploadDataset'e ver → listeyi tazele.
  Future<void> _uploadFile() async {
    final file = await pickCsvOrExcelFile();
    if (file == null) return; // kullanıcı iptal etti
    // Veri seti adını dosya adından türet (uzantıyı at).
    final name =
        file.name.replaceAll(RegExp(r'\.(csv|xlsx)$', caseSensitive: false), '');
    setState(() => _uploading = true);
    try {
      await ApiService.uploadDataset(
          name: name, bytes: file.bytes, filename: file.name);
      if (mounted) showSnack(context, '"$name" yüklendi.');
      _refresh();
    } catch (e) {
      if (mounted) showSnack(context, 'Yüklenemedi: $e', isError: true);
    } finally {
      if (mounted) setState(() => _uploading = false);
    }
  }

  // Örnek veri seti oluşturur (şema + satırlar) ki panel hemen denenebilsin.
  Future<void> _seedSample() async {
    setState(() => _seeding = true);
    try {
      await ApiService.seedSampleDataset();
      if (mounted) showSnack(context, 'Örnek veri seti eklendi.');
      _refresh();
    } catch (e) {
      if (mounted) showSnack(context, 'Örnek veri eklenemedi: $e', isError: true);
    } finally {
      if (mounted) setState(() => _seeding = false);
    }
  }

  // Yeniden adlandırma: mevcut adı dolu bir alanla sor, PUT ile güncelle (açıklama korunur).
  Future<void> _rename(Dataset d) async {
    final controller = TextEditingController(text: d.name);
    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Yeniden adlandır'),
        content: TextField(
          controller: controller,
          autofocus: true,
          decoration: const InputDecoration(labelText: 'Veri seti adı'),
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
    final name = controller.text.trim();
    controller.dispose();
    if (ok != true || name.isEmpty || name == d.name) return;
    try {
      await ApiService.renameDataset(d.id, name, description: d.description);
      if (!mounted) return;
      // Seçili veri setinin adı değiştiyse kabuktaki başlık eskiyi göstermesin.
      DatasetScope.of(context)?.onDatasetGone(d.id);
      showSnack(context, 'Ad "$name" olarak güncellendi.');
      _refresh();
    } catch (e) {
      if (!mounted) return;
      showSnack(context, 'Yeniden adlandırılamadı: $e', isError: true);
    }
  }

  // Silme: onay iste, DELETE ile sil (kolonlar+satırlar cascade), listeyi tazele.
  Future<void> _delete(Dataset d) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Veri setini sil'),
        content: Text(
            '"${d.name}" ve içindeki ${d.rowCount} satır kalıcı olarak silinecek. '
            'Bu işlem geri alınamaz.'),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx, false),
              child: const Text('Vazgeç')),
          FilledButton(
            style: FilledButton.styleFrom(backgroundColor: AppColors.danger),
            onPressed: () => Navigator.pop(ctx, true),
            child: const Text('Sil'),
          ),
        ],
      ),
    );
    if (ok != true) return;
    try {
      await ApiService.deleteDataset(d.id);
      if (!mounted) return;
      DatasetScope.of(context)?.onDatasetGone(d.id);
      showSnack(context, '"${d.name}" silindi.');
      _refresh();
    } catch (e) {
      if (!mounted) return;
      showSnack(context, 'Silinemedi: $e', isError: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    // Rol token'dan okunur. Yetkisi olmayan butonlar hiç çizilmez — backend zaten 403
    // döndürüyor, bu yalnız kullanıcının boşuna denememesi için (arayüz sadeliği).
    final canWrite = ApiService.canWrite;

    return FutureBuilder<List<Dataset>>(
      future: _future,
      builder: (context, snapshot) {
        final datasets = snapshot.data;
        final totalRows =
            datasets?.fold<int>(0, (sum, d) => sum + d.rowCount) ?? 0;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            PageHeader(
              title: 'Veri setleri',
              subtitle: datasets == null
                  ? 'Yükleniyor…'
                  : '${datasets.length} veri seti · toplam $totalRows satır',
              actions: [
                IconButton(
                  onPressed: _refresh,
                  icon: const Icon(Icons.refresh),
                  tooltip: 'Yenile',
                ),
                if (canWrite)
                  OutlinedButton.icon(
                    onPressed: _seeding ? null : _seedSample,
                    icon: _seeding
                        ? const SizedBox(
                            width: 16,
                            height: 16,
                            child: CircularProgressIndicator(strokeWidth: 2))
                        : const Icon(Icons.science_outlined, size: 18),
                    label: const Text('Örnek veri'),
                  ),
                if (canWrite)
                  FilledButton.icon(
                    onPressed: (_uploading || _seeding) ? null : _uploadFile,
                    icon: _uploading
                        ? const ButtonSpinner()
                        : const Icon(Icons.upload_file, size: 18),
                    label: const Text('Veri seti yükle'),
                  ),
              ],
            ),
            Expanded(child: _list(snapshot, canWrite)),
          ],
        );
      },
    );
  }

  Widget _list(AsyncSnapshot<List<Dataset>> snapshot, bool canWrite) {
    if (snapshot.connectionState != ConnectionState.done) {
      return const LoadingView(message: 'Veri setleri getiriliyor…');
    }
    if (snapshot.hasError) {
      return ErrorView(message: '${snapshot.error}', onRetry: _refresh);
    }

    final datasets = snapshot.data!;
    if (datasets.isEmpty) {
      return EmptyState(
        icon: Icons.folder_open_outlined,
        title: 'Henüz veri seti yok',
        message: canWrite
            ? 'Bir CSV veya Excel dosyası yükle; kolonlar ve tipleri otomatik algılanır. '
                'Denemek için örnek veri seti de ekleyebilirsin.'
            : 'Veri ekleme yetkin yok. Firmandaki bir editör veya yönetici veri seti yüklemeli.',
        action: canWrite
            ? FilledButton.icon(
                onPressed: _uploading ? null : _uploadFile,
                icon: const Icon(Icons.upload_file, size: 18),
                label: const Text('Veri seti yükle'),
              )
            : null,
      );
    }

    return ListView.separated(
      padding: const EdgeInsets.only(bottom: 12),
      itemCount: datasets.length,
      separatorBuilder: (_, _) => const SizedBox(height: 10),
      itemBuilder: (_, i) => _DatasetCard(
        dataset: datasets[i],
        // Her karta paletten sırayla bir renk: liste tek renk bloğu gibi durmasın.
        color: chartPalette[i % chartPalette.length],
        canWrite: canWrite,
        onOpen: () => widget.onOpen(datasets[i]),
        onRename: () => _rename(datasets[i]),
        onDelete: () => _delete(datasets[i]),
      ),
    );
  }
}

class _DatasetCard extends StatelessWidget {
  final Dataset dataset;
  final Color color;
  final bool canWrite;
  final VoidCallback onOpen;
  final VoidCallback onRename;
  final VoidCallback onDelete;

  const _DatasetCard({
    required this.dataset,
    required this.color,
    required this.canWrite,
    required this.onOpen,
    required this.onRename,
    required this.onDelete,
  });

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;
    final desc = dataset.description;

    return Card(
      child: InkWell(
        onTap: onOpen,
        hoverColor: AppColors.brand.withValues(alpha: 0.05),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 16),
          child: Row(
            children: [
              IconBadge(icon: Icons.table_chart_outlined, color: color),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(dataset.name,
                        style: t.titleMedium,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis),
                    const SizedBox(height: 3),
                    Text(
                      '${dataset.rowCount} satır'
                      '${desc != null && desc.isNotEmpty ? " · $desc" : ""}',
                      style: t.bodySmall,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 12),
              const Icon(Icons.chevron_right, color: AppColors.muted, size: 20),
              if (canWrite)
                PopupMenuButton<String>(
                  tooltip: 'İşlemler',
                  icon: const Icon(Icons.more_vert, size: 20),
                  onSelected: (v) => v == 'rename' ? onRename() : onDelete(),
                  itemBuilder: (_) => const [
                    PopupMenuItem(
                      value: 'rename',
                      child: ListTile(
                        dense: true,
                        contentPadding: EdgeInsets.zero,
                        leading: Icon(Icons.drive_file_rename_outline, size: 18),
                        title: Text('Yeniden adlandır'),
                      ),
                    ),
                    PopupMenuItem(
                      value: 'delete',
                      child: ListTile(
                        dense: true,
                        contentPadding: EdgeInsets.zero,
                        leading: Icon(Icons.delete_outline,
                            size: 18, color: AppColors.danger),
                        title: Text('Sil',
                            style: TextStyle(color: AppColors.danger)),
                      ),
                    ),
                  ],
                ),
            ],
          ),
        ),
      ),
    );
  }
}
