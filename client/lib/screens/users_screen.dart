import 'package:flutter/material.dart';
import '../api_service.dart';
import '../theme/app_theme.dart';
import '../widgets/ui.dart';

// Kullanıcı yönetimi (yalnız Admin): tenant'ın kullanıcılarını listeler, yeni kullanıcı
// ekler ve var olan bir kullanıcının rolünü değiştirir.
class UsersPage extends StatefulWidget {
  const UsersPage({super.key});

  @override
  State<UsersPage> createState() => _UsersPageState();
}

class _UsersPageState extends State<UsersPage> {
  late Future<List<AppUser>> _future;

  @override
  void initState() {
    super.initState();
    _future = ApiService.getUsers();
  }

  // Gövde blok `{}` olmalı: ok gövdeli closure atanan Future'ı döndürür, setState bunu reddeder.
  void _refresh() => setState(() {
        _future = ApiService.getUsers();
      });

  // Rol değiştirme. Backend son-Admin kuralını uygular (tek yönetici düşürülemez, 409);
  // gelen mesajı olduğu gibi gösteririz — kural tek yerde (sunucuda) yaşar.
  Future<void> _changeRole(AppUser user, String role) async {
    if (user.role == role) return;
    try {
      await ApiService.updateUserRole(user.id, role);
      if (!mounted) return;
      showSnack(context, '${user.email} artık ${roleLabels[role]}.');
      _refresh();
    } catch (e) {
      if (!mounted) return;
      showSnack(context, '$e', isError: true);
    }
  }

  // Yeni kullanıcı: e-posta + şifre + rol. Tenant token'dan gelir, burada seçilmez.
  Future<void> _addUser() async {
    final emailController = TextEditingController();
    final passwordController = TextEditingController();
    var role = 'Viewer';

    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => StatefulBuilder(
        // Diyalogun içindeki rol seçimi değişince yalnız diyalog yeniden çizilsin.
        builder: (ctx, setDialogState) => AlertDialog(
          title: const Text('Kullanıcı ekle'),
          content: SizedBox(
            width: 400,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                TextField(
                  controller: emailController,
                  autofocus: true,
                  keyboardType: TextInputType.emailAddress,
                  decoration: const InputDecoration(
                    labelText: 'E-posta',
                    prefixIcon: Icon(Icons.alternate_email, size: 18),
                  ),
                ),
                const SizedBox(height: 14),
                TextField(
                  controller: passwordController,
                  obscureText: true,
                  decoration: const InputDecoration(
                    labelText: 'Şifre',
                    helperText: 'En az 8 karakter',
                    prefixIcon: Icon(Icons.lock_outline, size: 18),
                  ),
                ),
                const SizedBox(height: 20),
                // Rol seçimi: üç seçenek de açıklamasıyla birlikte görünür.
                Align(
                  alignment: Alignment.centerLeft,
                  child: Text('Rol',
                      style: Theme.of(ctx).textTheme.labelMedium),
                ),
                const SizedBox(height: 8),
                for (final r in roleLabels.keys)
                  _RoleOption(
                    role: r,
                    selected: role == r,
                    onTap: () => setDialogState(() => role = r),
                  ),
              ],
            ),
          ),
          actions: [
            TextButton(
                onPressed: () => Navigator.pop(ctx, false),
                child: const Text('Vazgeç')),
            FilledButton(
                onPressed: () => Navigator.pop(ctx, true),
                child: const Text('Ekle')),
          ],
        ),
      ),
    );

    final email = emailController.text.trim();
    final password = passwordController.text;
    emailController.dispose();
    passwordController.dispose();
    if (ok != true || email.isEmpty || password.isEmpty) return;

    try {
      await ApiService.createUser(email: email, password: password, role: role);
      if (!mounted) return;
      showSnack(context, '$email eklendi (${roleLabels[role]}).');
      _refresh();
    } catch (e) {
      if (!mounted) return;
      showSnack(context, '$e', isError: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<List<AppUser>>(
      future: _future,
      builder: (context, snapshot) {
        final users = snapshot.data;
        final adminCount = users?.where((u) => u.role == 'Admin').length ?? 0;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            PageHeader(
              title: 'Kullanıcılar',
              subtitle: users == null
                  ? 'Yükleniyor…'
                  : '${users.length} kullanıcı · $adminCount yönetici',
              actions: [
                IconButton(
                  onPressed: _refresh,
                  icon: const Icon(Icons.refresh),
                  tooltip: 'Yenile',
                ),
                FilledButton.icon(
                  onPressed: _addUser,
                  icon: const Icon(Icons.person_add_alt, size: 18),
                  label: const Text('Kullanıcı ekle'),
                ),
              ],
            ),
            Expanded(child: _list(snapshot)),
          ],
        );
      },
    );
  }

  Widget _list(AsyncSnapshot<List<AppUser>> snapshot) {
    if (snapshot.connectionState != ConnectionState.done) {
      return const LoadingView(message: 'Kullanıcılar getiriliyor…');
    }
    if (snapshot.hasError) {
      return ErrorView(message: '${snapshot.error}', onRetry: _refresh);
    }

    final users = snapshot.data!;
    if (users.isEmpty) {
      return EmptyState(
        icon: Icons.group_outlined,
        title: 'Kullanıcı yok',
        message: 'Firmana çalışma arkadaşı ekleyerek başlayabilirsin.',
        action: FilledButton.icon(
          onPressed: _addUser,
          icon: const Icon(Icons.person_add_alt, size: 18),
          label: const Text('Kullanıcı ekle'),
        ),
      );
    }

    final currentUserId = ApiService.currentUserId;
    return ListView.separated(
      padding: const EdgeInsets.only(bottom: 12),
      itemCount: users.length,
      separatorBuilder: (_, _) => const SizedBox(height: 10),
      itemBuilder: (_, i) => _UserCard(
        user: users[i],
        isSelf: users[i].id == currentUserId,
        onChangeRole: (r) => _changeRole(users[i], r),
      ),
    );
  }
}

class _UserCard extends StatelessWidget {
  final AppUser user;
  final bool isSelf;
  final ValueChanged<String> onChangeRole;

  const _UserCard({
    required this.user,
    required this.isSelf,
    required this.onChangeRole,
  });

  static String _fmtDate(DateTime d) =>
      '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}.${d.year}';

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;
    final color = RoleBadge.colorOf(user.role);
    final created = user.createdAt;

    return Card(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 14),
        child: Row(
          children: [
            Container(
              width: 40,
              height: 40,
              alignment: Alignment.center,
              decoration: BoxDecoration(
                color: color.withValues(alpha: 0.16),
                shape: BoxShape.circle,
              ),
              child: Text(
                user.email.characters.first.toUpperCase(),
                style: TextStyle(
                    color: color, fontWeight: FontWeight.w700, fontSize: 16),
              ),
            ),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Flexible(
                        child: Text(user.email,
                            style: t.titleSmall,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis),
                      ),
                      if (isSelf)
                        Padding(
                          padding: const EdgeInsets.only(left: 8),
                          child: Text('siz', style: t.labelSmall),
                        ),
                    ],
                  ),
                  const SizedBox(height: 3),
                  Text(
                    '${roleDescriptions[user.role] ?? ''}'
                    '${created != null ? " · ${_fmtDate(created)} tarihinde eklendi" : ""}',
                    style: t.bodySmall,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ],
              ),
            ),
            const SizedBox(width: 12),
            RoleBadge(role: user.role),
            PopupMenuButton<String>(
              tooltip: 'Rolü değiştir',
              icon: const Icon(Icons.edit_outlined, size: 19),
              onSelected: onChangeRole,
              itemBuilder: (_) => [
                for (final r in roleLabels.keys)
                  PopupMenuItem(
                    value: r,
                    enabled: r != user.role,
                    child: Row(
                      children: [
                        Icon(
                          r == user.role
                              ? Icons.check_circle
                              : Icons.circle_outlined,
                          size: 17,
                          color: RoleBadge.colorOf(r),
                        ),
                        const SizedBox(width: 10),
                        Text(roleLabels[r]!),
                      ],
                    ),
                  ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

// Kullanıcı ekleme diyalogundaki rol seçeneği: ad + ne yapabildiği bir arada.
class _RoleOption extends StatelessWidget {
  final String role;
  final bool selected;
  final VoidCallback onTap;

  const _RoleOption({
    required this.role,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final color = RoleBadge.colorOf(role);
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(AppRadius.control),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
          decoration: BoxDecoration(
            color: selected ? color.withValues(alpha: 0.10) : AppColors.surfaceAlt,
            border: Border.all(
                color: selected ? color.withValues(alpha: 0.5) : AppColors.border),
            borderRadius: BorderRadius.circular(AppRadius.control),
          ),
          child: Row(
            children: [
              Icon(selected ? Icons.radio_button_checked : Icons.radio_button_off,
                  size: 18, color: selected ? color : AppColors.muted),
              const SizedBox(width: 12),
              Text(roleLabels[role]!,
                  style: TextStyle(
                      fontWeight: FontWeight.w600,
                      color: selected ? AppColors.text : AppColors.muted)),
              const SizedBox(width: 8),
              Expanded(
                child: Text(roleDescriptions[role] ?? '',
                    style: Theme.of(context).textTheme.bodySmall,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
