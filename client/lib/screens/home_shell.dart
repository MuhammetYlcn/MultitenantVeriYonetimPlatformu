import 'package:flutter/material.dart';
import '../api_service.dart';
import '../widgets/app_shell.dart';
import '../widgets/ui.dart';
import 'dashboard_screen.dart';
import 'data_table_screen.dart';
import 'datasets_screen.dart';
import 'login_screen.dart';
import 'users_screen.dart';

// Giriş sonrası TEK ekran. Hangi bölümün ve hangi veri setinin açık olduğunu burası
// bilir; alt sayfalar (veri setleri, tablo, panel, kullanıcılar) yalnız içerik üretir.
// Böylece sol menü sabit kalır, sağdaki alan değişir.
class HomeShell extends StatefulWidget {
  const HomeShell({super.key});

  @override
  State<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends State<HomeShell> {
  int _index = 0;

  // Üzerinde çalışılan veri seti. Veri setleri bölümünde bir karta tıklayınca dolar;
  // panel bölümü de aynı seçimi kullanır (iki bölüm arasında geçerken seçim korunur).
  Dataset? _selected;

  // Kullanıcı yönetimi yalnız Admin'e görünür → bölüm listesi role göre kurulur.
  List<String> get _keys => [
        'datasets',
        'dashboard',
        if (ApiService.isAdmin) 'users',
      ];

  List<ShellDestination> get _destinations => [
        const ShellDestination(
          icon: Icons.folder_outlined,
          activeIcon: Icons.folder,
          label: 'Veri setleri',
        ),
        const ShellDestination(
          icon: Icons.insights_outlined,
          activeIcon: Icons.insights,
          label: 'Panel',
        ),
        if (ApiService.isAdmin)
          const ShellDestination(
            icon: Icons.group_outlined,
            activeIcon: Icons.group,
            label: 'Kullanıcılar',
          ),
      ];

  Future<void> _logout() async {
    await ApiService.logout();
    if (!mounted) return;
    Navigator.pushAndRemoveUntil(
      context,
      MaterialPageRoute(builder: (_) => const LoginScreen()),
      (route) => false,
    );
  }

  void _openDataset(Dataset d) => setState(() {
        _selected = d;
        _index = 0; // tablo, veri setleri bölümünün içinde açılır
      });

  void _closeDataset() => setState(() => _selected = null);

  void _openDashboard() => setState(() => _index = 1);

  // Seçili veri seti silinirse ya da adı değişirse seçim tazelenir/temizlenir.
  void _onDatasetGone(String id) {
    if (_selected?.id == id) setState(() => _selected = null);
  }

  List<String> get _breadcrumb {
    final key = _keys[_index];
    final name = _selected?.name;
    return switch (key) {
      'datasets' => ['Veri setleri', ?name],
      'dashboard' => ['Panel', ?name],
      _ => ['Kullanıcılar'],
    };
  }

  Widget get _body {
    switch (_keys[_index]) {
      case 'datasets':
        final selected = _selected;
        if (selected == null) {
          return DatasetsPage(onOpen: _openDataset);
        }
        return DataTablePage(
          dataset: selected,
          onBack: _closeDataset,
          onOpenDashboard: _openDashboard,
        );

      case 'dashboard':
        final selected = _selected;
        if (selected == null) {
          return EmptyState(
            icon: Icons.insights_outlined,
            title: 'Panel için bir veri seti seç',
            message:
                'Özet kartları ve grafikler seçili veri setine göre hesaplanır. '
                'Veri setleri bölümünden birini aç, sonra buraya dön.',
            action: FilledButton.icon(
              onPressed: () => setState(() => _index = 0),
              icon: const Icon(Icons.folder_outlined, size: 18),
              label: const Text('Veri setlerine git'),
            ),
          );
        }
        return DashboardPage(dataset: selected);

      default:
        return const UsersPage();
    }
  }

  @override
  Widget build(BuildContext context) {
    return AppShell(
      destinations: _destinations,
      index: _index,
      onSelect: (i) => setState(() => _index = i),
      onLogout: _logout,
      breadcrumb: _breadcrumb,
      child: DatasetScope(
        onDatasetGone: _onDatasetGone,
        child: _body,
      ),
    );
  }
}

/// Alt sayfaların "bu veri seti artık yok/değişti" haberini kabuğa iletmesini sağlar.
/// (Silinen veri seti seçili kalırsa panel bölümü boş bir kimliği sorgulardı.)
class DatasetScope extends InheritedWidget {
  final void Function(String datasetId) onDatasetGone;

  const DatasetScope({
    super.key,
    required this.onDatasetGone,
    required super.child,
  });

  static DatasetScope? of(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<DatasetScope>();

  @override
  bool updateShouldNotify(DatasetScope oldWidget) => false;
}
