import 'package:flutter/material.dart';
import '../api_service.dart';
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

  void _refresh() => setState(() => _future = ApiService.getDatasets());

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
            return const Center(child: Text('Henüz veri seti yok.'));
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
                rows: datasets
                    .map((d) => DataRow(cells: [
                          DataCell(Text(d.name)),
                          DataCell(Text(d.description ?? '—')),
                          DataCell(Text('${d.rowCount}')),
                        ]))
                    .toList(),
              ),
            ),
          );
        },
      ),
    );
  }
}
