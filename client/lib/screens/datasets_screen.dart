import 'package:flutter/material.dart';
import '../api_service.dart';
import 'dashboard_screen.dart';
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

  void _refresh() => setState(() => _future = ApiService.getDatasets());

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

  void _openDashboard(Dataset d) {
    Navigator.push(
      context,
      MaterialPageRoute(
        builder: (_) => DashboardScreen(datasetId: d.id, datasetName: d.name),
      ),
    );
  }

  void _logout() {
    ApiService.logout();
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
        onPressed: _seeding ? null : _seedSample,
        icon: _seeding
            ? const SizedBox(
                height: 18, width: 18, child: CircularProgressIndicator(strokeWidth: 2))
            : const Icon(Icons.add),
        label: const Text('Örnek veri seti'),
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
              child: Text('Henüz veri seti yok.\nBaşlamak için "Örnek veri seti" ekle.',
                  textAlign: TextAlign.center),
            );
          }

          // Yatay + dikey kaydırılabilir bir tablo.
          return SingleChildScrollView(
            scrollDirection: Axis.vertical,
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: DataTable(
                columns: const [
                  DataColumn(label: Text('Ad')),
                  DataColumn(label: Text('Açıklama')),
                  DataColumn(label: Text('Satır'), numeric: true),
                ],
                // Satıra tıklayınca o setin dashboard'u açılır.
                rows: datasets
                    .map((d) => DataRow(
                          onSelectChanged: (_) => _openDashboard(d),
                          cells: [
                            DataCell(Text(d.name)),
                            DataCell(Text(d.description ?? '—')),
                            DataCell(Text('${d.rowCount}')),
                          ],
                        ))
                    .toList(),
              ),
            ),
          );
        },
      ),
    );
  }
}
