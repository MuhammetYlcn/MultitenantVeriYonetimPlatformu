import 'package:flutter/material.dart';
import '../api_service.dart';

// Kullanıcı yönetimi (yalnız Admin): tenant'ın kullanıcılarını listeler, yeni kullanıcı
// ekler ve var olan bir kullanıcının rolünü değiştirir.
class UsersScreen extends StatefulWidget {
  const UsersScreen({super.key});

  @override
  State<UsersScreen> createState() => _UsersScreenState();
}

class _UsersScreenState extends State<UsersScreen> {
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

  void _snack(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(message)));
  }

  // Rol değiştirme. Backend son-Admin kuralını uygular (tek yönetici düşürülemez, 409);
  // gelen mesajı olduğu gibi gösteririz — kural tek yerde (sunucuda) yaşar.
  Future<void> _changeRole(AppUser user, String role) async {
    if (user.role == role) return;
    try {
      await ApiService.updateUserRole(user.id, role);
      _snack('${user.email} artık ${roleLabels[role]}.');
      _refresh();
    } catch (e) {
      _snack('$e');
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
          content: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(
                controller: emailController,
                autofocus: true,
                keyboardType: TextInputType.emailAddress,
                decoration: const InputDecoration(
                    labelText: 'E-posta', border: OutlineInputBorder()),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: passwordController,
                obscureText: true,
                decoration: const InputDecoration(
                    labelText: 'Şifre (en az 8 karakter)',
                    border: OutlineInputBorder()),
              ),
              const SizedBox(height: 12),
              DropdownButtonFormField<String>(
                initialValue: role,
                decoration:
                    const InputDecoration(labelText: 'Rol', border: OutlineInputBorder()),
                items: roleLabels.keys
                    .map((r) => DropdownMenuItem(
                          value: r,
                          child: Text('${roleLabels[r]} — ${roleDescriptions[r]}'),
                        ))
                    .toList(),
                onChanged: (v) => setDialogState(() => role = v!),
              ),
            ],
          ),
          actions: [
            TextButton(
                onPressed: () => Navigator.pop(ctx, false), child: const Text('İptal')),
            FilledButton(
                onPressed: () => Navigator.pop(ctx, true), child: const Text('Ekle')),
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
      _snack('$email eklendi (${roleLabels[role]}).');
      _refresh();
    } catch (e) {
      _snack('$e');
    }
  }

  // Rolü renkli bir rozetle göster — listede rol farkı bir bakışta okunsun.
  Widget _roleChip(String role) {
    final color = switch (role) {
      'Admin' => Colors.deepPurple,
      'Editor' => Colors.teal,
      _ => Colors.blueGrey,
    };
    return Chip(
      label: Text(roleLabels[role] ?? role,
          style: TextStyle(color: color, fontWeight: FontWeight.w600)),
      backgroundColor: color.withValues(alpha: 0.12),
      side: BorderSide(color: color.withValues(alpha: 0.4)),
      visualDensity: VisualDensity.compact,
    );
  }

  @override
  Widget build(BuildContext context) {
    final currentUserId = ApiService.currentUserId;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Kullanıcılar'),
        actions: [
          IconButton(
              onPressed: _refresh, icon: const Icon(Icons.refresh), tooltip: 'Yenile'),
        ],
      ),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: _addUser,
        icon: const Icon(Icons.person_add_alt),
        label: const Text('Kullanıcı ekle'),
      ),
      body: FutureBuilder<List<AppUser>>(
        future: _future,
        builder: (context, snapshot) {
          if (snapshot.connectionState != ConnectionState.done) {
            return const Center(child: CircularProgressIndicator());
          }
          if (snapshot.hasError) {
            return Center(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Text('Yüklenemedi: ${snapshot.error}',
                    style: const TextStyle(color: Colors.red),
                    textAlign: TextAlign.center),
              ),
            );
          }

          final users = snapshot.data!;
          return ListView.separated(
            padding: const EdgeInsets.all(12),
            itemCount: users.length,
            separatorBuilder: (_, _) => const SizedBox(height: 8),
            itemBuilder: (_, i) {
              final u = users[i];
              final isSelf = u.id == currentUserId;
              return Card(
                child: ListTile(
                  leading: CircleAvatar(
                    child: Text(u.email.characters.first.toUpperCase()),
                  ),
                  title: Row(
                    children: [
                      Flexible(child: Text(u.email, overflow: TextOverflow.ellipsis)),
                      if (isSelf)
                        const Padding(
                          padding: EdgeInsets.only(left: 8),
                          child: Text('(siz)', style: TextStyle(color: Colors.grey)),
                        ),
                    ],
                  ),
                  subtitle: Text(roleDescriptions[u.role] ?? ''),
                  trailing: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      _roleChip(u.role),
                      PopupMenuButton<String>(
                        tooltip: 'Rolü değiştir',
                        icon: const Icon(Icons.edit_outlined),
                        onSelected: (r) => _changeRole(u, r),
                        itemBuilder: (_) => roleLabels.keys
                            .map((r) => PopupMenuItem(
                                  value: r,
                                  enabled: r != u.role,
                                  child: Row(
                                    children: [
                                      Icon(
                                        r == u.role
                                            ? Icons.check
                                            : Icons.arrow_right_alt,
                                        size: 18,
                                      ),
                                      const SizedBox(width: 8),
                                      Text(roleLabels[r]!),
                                    ],
                                  ),
                                ))
                            .toList(),
                      ),
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
