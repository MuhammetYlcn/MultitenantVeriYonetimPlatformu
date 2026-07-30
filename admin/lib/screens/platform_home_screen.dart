import 'package:flutter/material.dart';
import '../platform_api.dart';
import '../theme/app_theme.dart';
import 'platform_login_screen.dart';

/// Platform panelinin tek ekranı: özet sayaçlar + firma listesi (askıya alma) +
/// denetim izi. Firma verisi gösteren hiçbir bölüm YOKTUR — backend zaten
/// göndermiyor, panelin gösterecek verisi de yok.
class PlatformHomeScreen extends StatefulWidget {
  const PlatformHomeScreen({super.key});

  @override
  State<PlatformHomeScreen> createState() => _PlatformHomeScreenState();
}

class _PlatformHomeScreenState extends State<PlatformHomeScreen> {
  late Future<_PanelData> _future;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  Future<_PanelData> _load() async {
    // Üç ucu paralel çağır: panel tek seferde dolsun.
    final results = await Future.wait([
      PlatformApi.getStats(),
      PlatformApi.getTenants(),
      PlatformApi.getAuditLog(),
    ]);
    return _PanelData(
      results[0] as PlatformStats,
      results[1] as List<TenantSummary>,
      results[2] as List<AuditEntry>,
    );
  }

  void _refresh() => setState(() => _future = _load());

  void _toLogin() {
    PlatformApi.logout();
    Navigator.of(context).pushReplacement(
        MaterialPageRoute(builder: (_) => const PlatformLoginScreen()));
  }

  Future<void> _toggleStatus(TenantSummary tenant) async {
    final suspending = tenant.isActive;

    // Sonucu olan bir işlem: önce açıkça onay al.
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(suspending ? 'Firmayı askıya al' : 'Firmayı etkinleştir'),
        content: Text(
          suspending
              ? '"${tenant.name}" askıya alınacak. Kullanıcıları giriş yapamayacak '
                  've açık oturumları kapanacak.\n\nVerileri SİLİNMEZ — işlem geri alınabilir.'
              : '"${tenant.name}" yeniden etkinleştirilecek. Kullanıcıları tekrar '
                  'giriş yapabilecek.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Vazgeç'),
          ),
          FilledButton(
            style: suspending
                ? FilledButton.styleFrom(backgroundColor: AppColors.danger)
                : null,
            onPressed: () => Navigator.pop(context, true),
            child: Text(suspending ? 'Askıya al' : 'Etkinleştir'),
          ),
        ],
      ),
    );

    if (confirmed != true) return;

    try {
      await PlatformApi.setTenantStatus(tenant.id, !tenant.isActive);
      if (!mounted) return;
      _showMessage(suspending
          ? '"${tenant.name}" askıya alındı.'
          : '"${tenant.name}" etkinleştirildi.');
      _refresh();
    } on Object catch (e) {
      if (!mounted) return;
      _showMessage(e.toString());
    }
  }

  Future<void> _changePassword() async {
    final changed = await showDialog<bool>(
      context: context,
      builder: (_) => const _ChangePasswordDialog(),
    );
    if (changed == true && mounted) {
      _showMessage('Şifre güncellendi. Ayar dosyasındaki açık şifreyi artık silebilirsiniz.');
    }
  }

  void _showMessage(String text) =>
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text)));

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: Row(
          children: [
            const Icon(Icons.shield_outlined, color: AppColors.brand, size: 20),
            const SizedBox(width: 10),
            const Text('Platform Paneli'),
            const SizedBox(width: 12),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
              decoration: BoxDecoration(
                color: AppColors.brand.withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(6),
              ),
              child: Text('İŞLETMECİ',
                  style: theme.textTheme.labelSmall
                      ?.copyWith(color: AppColors.brand)),
            ),
          ],
        ),
        actions: [
          if (PlatformApi.email != null)
            Padding(
              padding: const EdgeInsets.only(right: 4),
              child: Center(
                child: Text(PlatformApi.email!, style: theme.textTheme.bodySmall),
              ),
            ),
          IconButton(
            tooltip: 'Yenile',
            onPressed: _refresh,
            icon: const Icon(Icons.refresh),
          ),
          IconButton(
            tooltip: 'Şifre değiştir',
            onPressed: _changePassword,
            icon: const Icon(Icons.key_outlined),
          ),
          IconButton(
            tooltip: 'Çıkış',
            onPressed: _toLogin,
            icon: const Icon(Icons.logout),
          ),
          const SizedBox(width: 8),
        ],
      ),
      body: FutureBuilder<_PanelData>(
        future: _future,
        builder: (context, snapshot) {
          if (snapshot.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }
          if (snapshot.hasError) {
            return _ErrorView(
              message: snapshot.error.toString(),
              onRetry: _refresh,
              onLogin: PlatformApi.isLoggedIn ? null : _toLogin,
            );
          }

          final data = snapshot.data!;
          return SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 1180),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    _StatsRow(stats: data.stats),
                    const SizedBox(height: 20),
                    const _BoundaryNotice(),
                    const SizedBox(height: 20),
                    _TenantsCard(
                      tenants: data.tenants,
                      onToggle: _toggleStatus,
                    ),
                    const SizedBox(height: 20),
                    _AuditCard(entries: data.audit),
                    const SizedBox(height: 24),
                  ],
                ),
              ),
            ),
          );
        },
      ),
    );
  }
}

class _PanelData {
  final PlatformStats stats;
  final List<TenantSummary> tenants;
  final List<AuditEntry> audit;
  _PanelData(this.stats, this.tenants, this.audit);
}

// ---- Üst şerit: sayaçlar ----

class _StatsRow extends StatelessWidget {
  final PlatformStats stats;
  const _StatsRow({required this.stats});

  @override
  Widget build(BuildContext context) {
    final tiles = [
      _Tile('Firma', '${stats.tenantCount}', Icons.apartment_outlined,
          AppColors.brand),
      _Tile('Aktif', '${stats.activeTenantCount}', Icons.check_circle_outline,
          AppColors.accent),
      _Tile('Askıda', '${stats.suspendedTenantCount}', Icons.pause_circle_outline,
          stats.suspendedTenantCount > 0 ? AppColors.warning : AppColors.muted),
      _Tile('Kullanıcı', '${stats.userCount}', Icons.people_outline,
          AppColors.accent),
      _Tile('Veri seti', '${stats.datasetCount}', Icons.folder_outlined,
          AppColors.brand),
      _Tile('Satır', _thousands(stats.rowCount), Icons.table_rows_outlined,
          AppColors.muted),
    ];

    // Dar ekranda alt satıra sarsın; her kutu en az 150 px.
    return LayoutBuilder(builder: (context, constraints) {
      const spacing = 12.0;
      final perRow = constraints.maxWidth > 900 ? 6 : (constraints.maxWidth > 520 ? 3 : 2);
      final width = (constraints.maxWidth - spacing * (perRow - 1)) / perRow;

      return Wrap(
        spacing: spacing,
        runSpacing: spacing,
        children: tiles
            .map((t) => SizedBox(width: width, child: t))
            .toList(growable: false),
      );
    });
  }

  static String _thousands(int value) {
    final s = value.toString();
    final buffer = StringBuffer();
    for (var i = 0; i < s.length; i++) {
      if (i > 0 && (s.length - i) % 3 == 0) buffer.write('.');
      buffer.write(s[i]);
    }
    return buffer.toString();
  }
}

class _Tile extends StatelessWidget {
  final String label;
  final String value;
  final IconData icon;
  final Color color;
  const _Tile(this.label, this.value, this.icon, this.color);

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(icon, size: 16, color: color),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(label,
                      style: theme.textTheme.labelMedium,
                      overflow: TextOverflow.ellipsis),
                ),
              ],
            ),
            const SizedBox(height: 10),
            Text(value, style: theme.textTheme.headlineSmall),
          ],
        ),
      ),
    );
  }
}

// ---- KVKK sınırı bildirimi ----

class _BoundaryNotice extends StatelessWidget {
  const _BoundaryNotice();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppColors.surface,
        borderRadius: BorderRadius.circular(AppRadius.card),
        border: Border.all(color: AppColors.border),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(Icons.verified_user_outlined,
              size: 18, color: AppColors.accent),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('Veri sınırı', style: theme.textTheme.titleSmall),
                const SizedBox(height: 4),
                Text(
                  'Bu panel yalnızca firma bilgilerini ve SAYILARI görür. Veri seti '
                  'adları, kolon adları, satır içerikleri ve kullanıcı e-postaları '
                  'sunucu tarafından hiç gönderilmez — platform işletmecisi müşteri '
                  'verisine erişemez.',
                  style: theme.textTheme.bodySmall,
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

// ---- Firma listesi ----

class _TenantsCard extends StatelessWidget {
  final List<TenantSummary> tenants;
  final void Function(TenantSummary) onToggle;
  const _TenantsCard({required this.tenants, required this.onToggle});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Card(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 18, 20, 14),
            child: Row(
              children: [
                Text('Firmalar', style: theme.textTheme.titleLarge),
                const SizedBox(width: 10),
                Text('${tenants.length} kayıt', style: theme.textTheme.bodySmall),
              ],
            ),
          ),
          const Divider(),
          if (tenants.isEmpty)
            Padding(
              padding: const EdgeInsets.all(32),
              child: Center(
                child: Text('Henüz firma kaydı yok.',
                    style: theme.textTheme.bodySmall),
              ),
            )
          else
            // Geniş tablo dar ekranda yatay kaysın, sayfa gövdesi kaymasın.
            SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: ConstrainedBox(
                constraints: const BoxConstraints(minWidth: 900),
                child: DataTable(
                  columns: const [
                    DataColumn(label: Text('FİRMA')),
                    DataColumn(label: Text('DURUM')),
                    DataColumn(label: Text('KULLANICI'), numeric: true),
                    DataColumn(label: Text('VERİ SETİ'), numeric: true),
                    DataColumn(label: Text('SATIR'), numeric: true),
                    DataColumn(label: Text('KAYIT')),
                    DataColumn(label: Text('')),
                  ],
                  rows: tenants
                      .map((t) => DataRow(cells: [
                            DataCell(Column(
                              mainAxisAlignment: MainAxisAlignment.center,
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(t.name,
                                    style: theme.textTheme.titleSmall),
                                Text(t.slug, style: theme.textTheme.bodySmall),
                              ],
                            )),
                            DataCell(_StatusChip(tenant: t)),
                            DataCell(Text('${t.userCount}')),
                            DataCell(Text('${t.datasetCount}')),
                            DataCell(Text('${t.rowCount}')),
                            DataCell(Text(_date(t.createdAt),
                                style: theme.textTheme.bodySmall)),
                            DataCell(
                              t.isActive
                                  ? OutlinedButton.icon(
                                      onPressed: () => onToggle(t),
                                      icon: const Icon(
                                          Icons.pause_circle_outline, size: 16),
                                      label: const Text('Askıya al'),
                                      style: OutlinedButton.styleFrom(
                                        foregroundColor: AppColors.warning,
                                        side: const BorderSide(
                                            color: AppColors.border),
                                      ),
                                    )
                                  : FilledButton.icon(
                                      onPressed: () => onToggle(t),
                                      icon: const Icon(
                                          Icons.play_circle_outline, size: 16),
                                      label: const Text('Etkinleştir'),
                                    ),
                            ),
                          ]))
                      .toList(),
                ),
              ),
            ),
        ],
      ),
    );
  }

  static String _date(DateTime? value) {
    if (value == null) return '—';
    final local = value.toLocal();
    return '${local.day.toString().padLeft(2, '0')}.'
        '${local.month.toString().padLeft(2, '0')}.${local.year}';
  }
}

class _StatusChip extends StatelessWidget {
  final TenantSummary tenant;
  const _StatusChip({required this.tenant});

  @override
  Widget build(BuildContext context) {
    final active = tenant.isActive;
    final color = active ? AppColors.accent : AppColors.warning;

    return Tooltip(
      message: active
          ? 'Kullanıcıları giriş yapabilir.'
          : 'Askıya alındı — giriş ve oturum yenileme kapalı.',
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.14),
          borderRadius: BorderRadius.circular(999),
          border: Border.all(color: color.withValues(alpha: 0.5)),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(active ? Icons.check_circle : Icons.pause_circle,
                size: 13, color: color),
            const SizedBox(width: 6),
            Text(active ? 'Aktif' : 'Askıda',
                style: TextStyle(
                    color: color, fontSize: 12, fontWeight: FontWeight.w700)),
          ],
        ),
      ),
    );
  }
}

// ---- Denetim izi ----

class _AuditCard extends StatelessWidget {
  final List<AuditEntry> entries;
  const _AuditCard({required this.entries});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Card(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 18, 20, 14),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('Denetim izi', style: theme.textTheme.titleLarge),
                const SizedBox(height: 2),
                Text('Panelde yapılan her işlem kayda geçer.',
                    style: theme.textTheme.bodySmall),
              ],
            ),
          ),
          const Divider(),
          if (entries.isEmpty)
            Padding(
              padding: const EdgeInsets.all(28),
              child: Center(
                child: Text('Kayıt yok.', style: theme.textTheme.bodySmall),
              ),
            )
          else
            ...entries.map((e) => _AuditRow(entry: e)),
        ],
      ),
    );
  }
}

class _AuditRow extends StatelessWidget {
  final AuditEntry entry;
  const _AuditRow({required this.entry});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final label = actionLabels[entry.action] ?? entry.action;
    final isSuspend = entry.action == 'TenantSuspended';

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 11),
      child: Row(
        children: [
          Icon(
            isSuspend ? Icons.pause_circle_outline : Icons.circle_outlined,
            size: 15,
            color: isSuspend ? AppColors.warning : AppColors.muted,
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Text.rich(TextSpan(children: [
              TextSpan(text: label, style: theme.textTheme.bodyMedium),
              if (entry.tenantName != null)
                TextSpan(
                  text: '  ·  ${entry.tenantName}',
                  style: theme.textTheme.bodyMedium
                      ?.copyWith(color: AppColors.brand),
                ),
            ])),
          ),
          Text(entry.adminEmail, style: theme.textTheme.bodySmall),
          const SizedBox(width: 16),
          SizedBox(
            width: 130,
            child: Text(_time(entry.createdAt),
                style: theme.textTheme.bodySmall, textAlign: TextAlign.right),
          ),
        ],
      ),
    );
  }

  static String _time(DateTime? value) {
    if (value == null) return '—';
    final l = value.toLocal();
    return '${l.day.toString().padLeft(2, '0')}.${l.month.toString().padLeft(2, '0')}.'
        '${l.year}  ${l.hour.toString().padLeft(2, '0')}:'
        '${l.minute.toString().padLeft(2, '0')}';
  }
}

// ---- Şifre değiştirme ----

class _ChangePasswordDialog extends StatefulWidget {
  const _ChangePasswordDialog();

  @override
  State<_ChangePasswordDialog> createState() => _ChangePasswordDialogState();
}

class _ChangePasswordDialogState extends State<_ChangePasswordDialog> {
  final _formKey = GlobalKey<FormState>();
  final _current = TextEditingController();
  final _next = TextEditingController();
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _current.dispose();
    _next.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await PlatformApi.changePassword(_current.text, _next.text);
      if (mounted) Navigator.pop(context, true);
    } on Object catch (e) {
      if (mounted) setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return AlertDialog(
      title: const Text('Şifre değiştir'),
      content: SizedBox(
        width: 380,
        child: Form(
          key: _formKey,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                'Kurulumda ayar dosyasına yazılan şifreyi burada değiştirin; '
                'sonrasında dosyadaki açık değeri silebilirsiniz.',
                style: theme.textTheme.bodySmall,
              ),
              const SizedBox(height: 18),
              TextFormField(
                controller: _current,
                obscureText: true,
                decoration: const InputDecoration(labelText: 'Mevcut şifre'),
                validator: (v) =>
                    (v == null || v.isEmpty) ? 'Mevcut şifre gerekli.' : null,
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _next,
                obscureText: true,
                decoration: const InputDecoration(labelText: 'Yeni şifre'),
                validator: (v) => (v == null || v.length < 8)
                    ? 'Yeni şifre en az 8 karakter olmalı.'
                    : null,
              ),
              if (_error != null) ...[
                const SizedBox(height: 14),
                Text(_error!,
                    style:
                        TextStyle(color: theme.colorScheme.error, fontSize: 13)),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: _busy ? null : () => Navigator.pop(context, false),
          child: const Text('Vazgeç'),
        ),
        FilledButton(
          onPressed: _busy ? null : _submit,
          child: _busy
              ? const SizedBox(
                  height: 16,
                  width: 16,
                  child: CircularProgressIndicator(
                      strokeWidth: 2, color: Colors.white))
              : const Text('Kaydet'),
        ),
      ],
    );
  }
}

// ---- Hata görünümü ----

class _ErrorView extends StatelessWidget {
  final String message;
  final VoidCallback onRetry;
  final VoidCallback? onLogin;
  const _ErrorView(
      {required this.message, required this.onRetry, this.onLogin});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: Card(
          child: Padding(
            padding: const EdgeInsets.all(28),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(Icons.error_outline,
                    size: 32, color: theme.colorScheme.error),
                const SizedBox(height: 14),
                Text(message,
                    textAlign: TextAlign.center,
                    style: theme.textTheme.bodyMedium),
                const SizedBox(height: 20),
                if (onLogin != null)
                  FilledButton(
                      onPressed: onLogin, child: const Text('Giriş ekranına dön'))
                else
                  OutlinedButton.icon(
                    onPressed: onRetry,
                    icon: const Icon(Icons.refresh, size: 16),
                    label: const Text('Tekrar dene'),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
