import 'package:flutter/material.dart';
import 'package:flutter/services.dart'; // Clipboard — bağlantıyı panoya kopyalamak için
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

  // Kullanıcı DAVET et: yalnız e-posta + rol. Şifre alanı bilinçli olarak YOK —
  // şifreyi kullanıcı, davet bağlantısını açtığında kendisi belirler. Böylece Admin
  // hiçbir aşamada başkasının şifresini bilmez.
  Future<void> _inviteUser() async {
    final emailController = TextEditingController();
    var role = 'Viewer';

    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => StatefulBuilder(
        // Diyalogun içindeki rol seçimi değişince yalnız diyalog yeniden çizilsin.
        builder: (ctx, setDialogState) => AlertDialog(
          title: const Text('Kullanıcı davet et'),
          content: SizedBox(
            width: 400,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: AppColors.surfaceAlt,
                    borderRadius: BorderRadius.circular(AppRadius.control),
                    border: Border.all(color: AppColors.border),
                  ),
                  child: Row(
                    children: [
                      const Icon(Icons.lock_outline,
                          size: 17, color: AppColors.muted),
                      const SizedBox(width: 10),
                      Expanded(
                        child: Text(
                          'Şifreyi kullanıcı kendisi belirler. Siz yalnızca bir '
                          'davet bağlantısı üretirsiniz.',
                          style: Theme.of(ctx).textTheme.bodySmall,
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 16),
                TextField(
                  controller: emailController,
                  autofocus: true,
                  keyboardType: TextInputType.emailAddress,
                  decoration: const InputDecoration(
                    labelText: 'E-posta',
                    prefixIcon: Icon(Icons.alternate_email, size: 18),
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
                child: const Text('Davet oluştur')),
          ],
        ),
      ),
    );

    final email = emailController.text.trim();
    emailController.dispose();
    if (ok != true || email.isEmpty) return;

    try {
      final link = await ApiService.inviteUser(email: email, role: role);
      if (!mounted) return;
      await _showLink(link);
      if (!mounted) return;
      _refresh();
    } catch (e) {
      if (!mounted) return;
      showSnack(context, '$e', isError: true);
    }
  }

  // Var olan bir kullanıcı için şifre sıfırlama bağlantısı üretir. Admin yeni şifreyi
  // GÖRMEZ; yalnızca tek kullanımlık bağlantıyı kullanıcıya iletir.
  Future<void> _resetPassword(AppUser user) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Şifre sıfırlama bağlantısı'),
        content: Text(
          '${user.email} için tek kullanımlık bir bağlantı üretilecek. '
          'Yeni şifreyi kullanıcı kendisi belirleyecek — siz görmeyeceksiniz.\n\n'
          'Bağlantı 2 saat geçerlidir ve kullanılınca kullanıcının açık '
          'oturumları kapanır.',
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx, false),
              child: const Text('Vazgeç')),
          FilledButton(
              onPressed: () => Navigator.pop(ctx, true),
              child: const Text('Bağlantı üret')),
        ],
      ),
    );
    if (confirmed != true) return;

    try {
      final link = await ApiService.createPasswordReset(user.id);
      if (!mounted) return;
      await _showLink(link);
    } catch (e) {
      if (!mounted) return;
      showSnack(context, '$e', isError: true);
    }
  }

  // Üretilen bağlantıyı gösterir. E-posta gönderimi (SMTP) kapsam dışı olduğundan
  // bağlantı Admin'e ekranda verilir; Admin bunu kullanıcıya kendi kanalından iletir.
  // Bağlantı YALNIZCA burada bir kez görünür — sunucuda yalnız özeti saklanıyor.
  Future<void> _showLink(AccountLink link) async {
    final url = link.url(Uri.base.origin);
    final isInvite = link.purpose == 'Invite';

    await showDialog<void>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(isInvite ? 'Davet bağlantısı hazır' : 'Sıfırlama bağlantısı hazır'),
        content: SizedBox(
          width: 460,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                isInvite
                    ? '${link.email} adresine iletin. Kullanıcı bu bağlantıdan '
                        'şifresini belirleyip hesabını açacak.'
                    : '${link.email} adresine iletin. Kullanıcı bu bağlantıdan '
                        'yeni şifresini belirleyecek.',
                style: Theme.of(ctx).textTheme.bodyMedium,
              ),
              const SizedBox(height: 14),
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: AppColors.surfaceAlt,
                  borderRadius: BorderRadius.circular(AppRadius.control),
                  border: Border.all(color: AppColors.border),
                ),
                child: SelectableText(url,
                    style: const TextStyle(fontSize: 12.5, height: 1.5)),
              ),
              const SizedBox(height: 10),
              Text(
                'Bu bağlantı tek kullanımlıktır ve yalnız şimdi görünür — '
                'sunucuda saklanmaz.',
                style: Theme.of(ctx).textTheme.bodySmall,
              ),
            ],
          ),
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx),
              child: const Text('Kapat')),
          FilledButton.icon(
            onPressed: () {
              Clipboard.setData(ClipboardData(text: url));
              Navigator.pop(ctx);
              showSnack(context, 'Bağlantı panoya kopyalandı.');
            },
            icon: const Icon(Icons.copy, size: 16),
            label: const Text('Kopyala'),
          ),
        ],
      ),
    );
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
                  onPressed: _inviteUser,
                  icon: const Icon(Icons.person_add_alt, size: 18),
                  label: const Text('Kullanıcı davet et'),
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
        message: 'Firmana çalışma arkadaşı davet ederek başlayabilirsin.',
        action: FilledButton.icon(
          onPressed: _inviteUser,
          icon: const Icon(Icons.person_add_alt, size: 18),
          label: const Text('Kullanıcı davet et'),
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
        onResetPassword: () => _resetPassword(users[i]),
      ),
    );
  }
}

class _UserCard extends StatelessWidget {
  final AppUser user;
  final bool isSelf;
  final ValueChanged<String> onChangeRole;
  final VoidCallback onResetPassword;

  const _UserCard({
    required this.user,
    required this.isSelf,
    required this.onChangeRole,
    required this.onResetPassword,
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
            // Şifre sıfırlama yalnız BAĞLANTI üretir; kendi şifreni buradan değil
            // profil menüsündeki "Şifre değiştir"den değiştirirsin.
            if (!isSelf)
              IconButton(
                tooltip: 'Şifre sıfırlama bağlantısı üret',
                icon: const Icon(Icons.key_outlined, size: 19),
                onPressed: onResetPassword,
              ),
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
