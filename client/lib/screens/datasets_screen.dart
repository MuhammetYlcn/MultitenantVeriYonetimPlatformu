import 'package:flutter/material.dart';
import 'package:file_picker/file_picker.dart';
import '../api_service.dart';
import 'data_table_screen.dart';
import 'login_screen.dart';

// Giriş sonrası ana ekran: tenant'ın veri setlerini bir tabloda listeler.
class DatasetsScreen extends StatefulWidget {
  const DatasetsScreen({super.key});

  @override
  State<DatasetsScreen> createState() => _DatasetsScreenState();
}

class _DatasetsScreenState extends State<DatasetsScreen> {
  // Future'ı bir kez oluşturup FutureBuilder'a veriyoruz; yenilemede yeniden kurulur.
  late Future<List<Dataset>> _future;

  @override
  void initState() {
    super.initState();
    _future = ApiService.getDatasets();
  }

  bool _seeding = false;
  bool _uploading = false;

  void _refresh() => setState(() => _future = ApiService.getDatasets());

  // Gerçek CSV/Excel yükleme: dosya seç → byte'ları uploadDataset'e ver → listeyi tazele.
  Future<void> _uploadFile() async {
    final result = await FilePicker.pickFiles(
      type: FileType.custom,
      allowedExtensions: ['csv', 'xlsx'],
      withData: true, // web dahil tüm platformlarda dosya byte'larını getir
    );
    if (result == null) return; // kullanıcı iptal etti
    final file = result.files.single;
    final bytes = file.bytes;
    if (bytes == null) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(const SnackBar(content: Text('Dosya okunamadı.')));
      }
      return;
    }
    // Veri seti adını dosya adından türet (uzantıyı at). İsimlendirme/yönetim ayrı adımda.
    final name =
        file.name.replaceAll(RegExp(r'\.(csv|xlsx)$', caseSensitive: false), '');
    setState(() => _uploading = true);
    try {
      await ApiService.uploadDataset(name: name, bytes: bytes, filename: file.name);
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text('"$name" yüklendi.')));
      }
      _refresh();
    } catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text('Yüklenemedi: $e')));
      }
    } finally {
      if (mounted) setState(() => _uploading = false);
    }
  }

  // Örnek veri seti oluşturur (şema + satırlar) ki dashboard denenebilsin.
  Future<void> _seedSample() async {
    setState(() => _seeding = true);
    try {
      await ApiService.seedSampleDataset();
      _refresh();
    } catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text('Örnek veri eklenemedi: $e')));
      }
    } finally {
      if (mounted) setState(() => _seeding = false);
    }
  }

  void _openData(Dataset d) {
    Navigator.push(
      context,
      MaterialPageRoute(
        builder: (_) => DataTableScreen(datasetId: d.id, datasetName: d.name),
      ),
    );
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
          decoration: const InputDecoration(
              labelText: 'Veri seti adı', border: OutlineInputBorder()),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('İptal')),
          FilledButton(onPressed: () => Navigator.pop(ctx, true), child: const Text('Kaydet')),
        ],
      ),
    );
    final name = controller.text.trim();
    controller.dispose();
    if (ok != true || name.isEmpty || name == d.name) return;
    try {
      await ApiService.renameDataset(d.id, name, description: d.description);
      if (!mounted) return;
      _refresh();
    } catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text('Yeniden adlandırılamadı: $e')));
    }
  }

  // Silme: onay iste, DELETE ile sil (kolonlar+satırlar cascade), listeyi tazele.
  Future<void> _delete(Dataset d) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Veri setini sil'),
        content: Text('"${d.name}" ve tüm satırları kalıcı olarak silinecek. Emin misin?'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('İptal')),
          FilledButton(
            style: FilledButton.styleFrom(backgroundColor: Colors.red),
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
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text('"${d.name}" silindi.')));
      _refresh();
    } catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Silinemedi: $e')));
    }
  }

  Future<void> _logout() async {
    await ApiService.logout();
    // await'ten sonra bu ekran hâlâ ağaçta mı? Değilse context'i kullanmak hatalı olur.
    if (!mounted) return;
    Navigator.pushAndRemoveUntil(
      context,
      MaterialPageRoute(builder: (_) => const LoginScreen()),
      (route) => false,
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Veri Setleri'),
        actions: [
          IconButton(
            onPressed: _seeding ? null : _seedSample,
            icon: _seeding
                ? const SizedBox(
                    height: 18, width: 18, child: CircularProgressIndicator(strokeWidth: 2))
                : const Icon(Icons.science_outlined),
            tooltip: 'Örnek veri ekle',
          ),
          IconButton(
            onPressed: _refresh,
            icon: const Icon(Icons.refresh),
            tooltip: 'Yenile',
          ),
          IconButton(
            onPressed: _logout,
            icon: const Icon(Icons.logout),
            tooltip: 'Çıkış',
          ),
        ],
      ),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: (_uploading || _seeding) ? null : _uploadFile,
        icon: _uploading
            ? const SizedBox(
                height: 18, width: 18, child: CircularProgressIndicator(strokeWidth: 2))
            : const Icon(Icons.upload_file),
        label: const Text('Veri seti yükle'),
      ),
      // FutureBuilder: bir Future'ın durumuna (bekliyor / hata / hazır) göre farklı
      // widget çizer. C#'ta await + üç duruma göre UI güncellemenin bildirimsel hâli.
      body: FutureBuilder<List<Dataset>>(
        future: _future,
        builder: (context, snapshot) {
          if (snapshot.connectionState != ConnectionState.done) {
            return const Center(child: CircularProgressIndicator());
          }
          if (snapshot.hasError) {
            return Center(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Text(
                  'Yüklenemedi: ${snapshot.error}',
                  style: const TextStyle(color: Colors.red),
                  textAlign: TextAlign.center,
                ),
              ),
            );
          }

          final datasets = snapshot.data!;
          if (datasets.isEmpty) {
            return const Center(
              child: Text(
                  'Henüz veri seti yok.\n"Veri seti yükle" ile bir CSV/Excel dosyası ekle.',
                  textAlign: TextAlign.center),
            );
          }

          // Her veri seti bir kart: tıkla → verileri aç; sağdaki menü → yeniden adlandır / sil.
          return ListView.separated(
            padding: const EdgeInsets.all(12),
            itemCount: datasets.length,
            separatorBuilder: (_, _) => const SizedBox(height: 8),
            itemBuilder: (_, i) {
              final d = datasets[i];
              final desc = d.description;
              return Card(
                child: ListTile(
                  title: Text(d.name),
                  subtitle: Text('${d.rowCount} satır'
                      '${desc != null && desc.isNotEmpty ? " · $desc" : ""}'),
                  onTap: () => _openData(d),
                  trailing: PopupMenuButton<String>(
                    tooltip: 'İşlemler',
                    onSelected: (v) => v == 'rename' ? _rename(d) : _delete(d),
                    itemBuilder: (_) => const [
                      PopupMenuItem(value: 'rename', child: Text('Yeniden adlandır')),
                      PopupMenuItem(value: 'delete', child: Text('Sil')),
                    ],
                  ),
                ),
              );
            },
          );
        },
      ),
    );
  }
}
