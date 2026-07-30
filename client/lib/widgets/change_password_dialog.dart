import 'package:flutter/material.dart';
import '../api_service.dart';

/// Kullanıcının kendi şifresini değiştirdiği diyalog.
///
/// Mevcut şifre ZORUNLU: açık bırakılmış bir oturumu ele geçiren kişi, şifreyi
/// değiştirip erişimi kalıcı hâle getirememeli.
///
/// Başarılı olduğunda sunucu bu kullanıcının tüm refresh token'larını iptal eder;
/// çağıran ekran kullanıcıyı giriş ekranına yönlendirir.
class ChangePasswordDialog extends StatefulWidget {
  const ChangePasswordDialog({super.key});

  @override
  State<ChangePasswordDialog> createState() => _ChangePasswordDialogState();
}

class _ChangePasswordDialogState extends State<ChangePasswordDialog> {
  final _formKey = GlobalKey<FormState>();
  final _current = TextEditingController();
  final _next = TextEditingController();
  final _confirm = TextEditingController();
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _current.dispose();
    _next.dispose();
    _confirm.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;

    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await ApiService.changePassword(
          currentPassword: _current.text, newPassword: _next.text);
      if (mounted) Navigator.pop(context, true);
    } catch (e) {
      if (mounted) setState(() => _error = '$e');
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
        width: 400,
        child: Form(
          key: _formKey,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                controller: _current,
                obscureText: true,
                autofocus: true,
                decoration: const InputDecoration(
                  labelText: 'Mevcut şifre',
                  prefixIcon: Icon(Icons.lock_outline, size: 18),
                ),
                validator: (v) =>
                    (v == null || v.isEmpty) ? 'Mevcut şifre gerekli.' : null,
              ),
              const SizedBox(height: 14),
              TextFormField(
                controller: _next,
                obscureText: true,
                decoration: const InputDecoration(
                  labelText: 'Yeni şifre',
                  helperText: 'En az 8 karakter',
                  prefixIcon: Icon(Icons.lock_reset, size: 18),
                ),
                validator: (v) => (v == null || v.length < 8)
                    ? 'Yeni şifre en az 8 karakter olmalı.'
                    : null,
              ),
              const SizedBox(height: 14),
              TextFormField(
                controller: _confirm,
                obscureText: true,
                decoration: const InputDecoration(
                  labelText: 'Yeni şifre (tekrar)',
                  prefixIcon: Icon(Icons.lock_reset, size: 18),
                ),
                onFieldSubmitted: (_) => _submit(),
                validator: (v) =>
                    v != _next.text ? 'Şifreler aynı değil.' : null,
              ),
              const SizedBox(height: 14),
              Text(
                'Şifre değişince güvenlik için tüm oturumların kapanır ve '
                'yeniden giriş yapman gerekir.',
                style: theme.textTheme.bodySmall,
              ),
              if (_error != null) ...[
                const SizedBox(height: 14),
                Text(_error!,
                    style: TextStyle(color: theme.colorScheme.error, fontSize: 13)),
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
