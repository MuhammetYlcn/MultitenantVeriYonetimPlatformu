import 'package:flutter/material.dart';
import '../api_service.dart';
import '../theme/app_theme.dart';
import 'login_screen.dart';

/// Davet / şifre sıfırlama bağlantısının açıldığı ekran (`#/davet/<token>`).
///
/// Giriş GEREKTİRMEZ: davet edilen kişinin henüz hesabı yoktur, şifresini unutan
/// kişi de giriş yapamaz. Erişimi bağlantıdaki tek kullanımlık token yetkilendirir.
///
/// Bu ekranın varlık sebebi tek cümle: şifreyi kullanıcı KENDİSİ belirlesin, Admin
/// hiçbir zaman bilmesin.
class AcceptInvitationScreen extends StatefulWidget {
  final String token;
  const AcceptInvitationScreen({super.key, required this.token});

  @override
  State<AcceptInvitationScreen> createState() => _AcceptInvitationScreenState();
}

class _AcceptInvitationScreenState extends State<AcceptInvitationScreen> {
  final _formKey = GlobalKey<FormState>();
  final _password = TextEditingController();
  final _confirm = TextEditingController();

  late Future<AccountLinkInfo> _info;
  bool _busy = false;
  bool _done = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    // Token'ı harcamadan bağlamı okur: "X firmasına Editör olarak davet edildiniz".
    _info = ApiService.inspectInvitation(widget.token);
  }

  @override
  void dispose() {
    _password.dispose();
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
      await ApiService.acceptInvitation(widget.token, _password.text);
      if (mounted) setState(() => _done = true);
    } catch (e) {
      if (mounted) setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _toLogin() => Navigator.of(context)
      .pushReplacement(MaterialPageRoute(builder: (_) => const LoginScreen()));

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: Center(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 440),
            child: Card(
              child: Padding(
                padding: const EdgeInsets.all(32),
                child: _body(),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _body() {
    if (_done) return _successView();

    return FutureBuilder<AccountLinkInfo>(
      future: _info,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Padding(
            padding: EdgeInsets.all(24),
            child: Center(child: CircularProgressIndicator()),
          );
        }
        if (snapshot.hasError) return _invalidLinkView('${snapshot.error}');
        return _formView(snapshot.data!);
      },
    );
  }

  Widget _formView(AccountLinkInfo info) {
    final theme = Theme.of(context);

    return Form(
      key: _formKey,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            info.isInvite ? 'Hesabını oluştur' : 'Yeni şifreni belirle',
            style: theme.textTheme.headlineSmall,
          ),
          const SizedBox(height: 6),
          Text(
            info.isInvite
                ? '${info.tenantName} firmasına '
                    '${roleLabels[info.role] ?? info.role} olarak davet edildin.'
                : '${info.tenantName} firmasındaki hesabın için yeni bir şifre belirle.',
            style: theme.textTheme.bodyMedium,
          ),
          const SizedBox(height: 18),

          // Hedef e-posta değiştirilemez: bağlantı zaten belirli bir adrese üretildi.
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
            decoration: BoxDecoration(
              color: AppColors.surfaceAlt,
              borderRadius: BorderRadius.circular(AppRadius.control),
              border: Border.all(color: AppColors.border),
            ),
            child: Row(
              children: [
                const Icon(Icons.alternate_email, size: 17, color: AppColors.muted),
                const SizedBox(width: 10),
                Expanded(
                    child: Text(info.email, style: theme.textTheme.bodyMedium)),
              ],
            ),
          ),
          const SizedBox(height: 20),

          TextFormField(
            controller: _password,
            obscureText: true,
            autofocus: true,
            decoration: const InputDecoration(
              labelText: 'Şifre',
              helperText: 'En az 8 karakter',
              prefixIcon: Icon(Icons.lock_outline, size: 18),
            ),
            validator: (v) =>
                (v == null || v.length < 8) ? 'Şifre en az 8 karakter olmalı.' : null,
          ),
          const SizedBox(height: 14),
          TextFormField(
            controller: _confirm,
            obscureText: true,
            decoration: const InputDecoration(
              labelText: 'Şifre (tekrar)',
              prefixIcon: Icon(Icons.lock_outline, size: 18),
            ),
            onFieldSubmitted: (_) => _submit(),
            validator: (v) =>
                v != _password.text ? 'Şifreler aynı değil.' : null,
          ),

          if (_error != null) ...[
            const SizedBox(height: 16),
            _ErrorBox(message: _error!),
          ],

          const SizedBox(height: 24),
          FilledButton(
            onPressed: _busy ? null : _submit,
            child: _busy
                ? const SizedBox(
                    height: 18,
                    width: 18,
                    child: CircularProgressIndicator(
                        strokeWidth: 2, color: Colors.white))
                : Text(info.isInvite ? 'Hesabı oluştur' : 'Şifreyi güncelle'),
          ),
          const SizedBox(height: 10),
          Text(
            'Bu şifreyi yalnızca sen bilirsin; yöneticin dâhil kimse göremez.',
            style: theme.textTheme.bodySmall,
            textAlign: TextAlign.center,
          ),
        ],
      ),
    );
  }

  Widget _successView() {
    final theme = Theme.of(context);
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const Icon(Icons.check_circle_outline, size: 40, color: AppColors.accent),
        const SizedBox(height: 16),
        Text('Hazır', style: theme.textTheme.headlineSmall, textAlign: TextAlign.center),
        const SizedBox(height: 8),
        Text('Şifren belirlendi. Artık giriş yapabilirsin.',
            style: theme.textTheme.bodyMedium, textAlign: TextAlign.center),
        const SizedBox(height: 24),
        FilledButton(onPressed: _toLogin, child: const Text('Giriş ekranına git')),
      ],
    );
  }

  Widget _invalidLinkView(String message) {
    final theme = Theme.of(context);
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Icon(Icons.link_off, size: 40, color: theme.colorScheme.error),
        const SizedBox(height: 16),
        Text('Bağlantı çalışmıyor',
            style: theme.textTheme.headlineSmall, textAlign: TextAlign.center),
        const SizedBox(height: 8),
        Text(message, style: theme.textTheme.bodyMedium, textAlign: TextAlign.center),
        const SizedBox(height: 10),
        Text(
          'Bağlantılar tek kullanımlıktır ve süreleri doludur. '
          'Yöneticinden yeni bir bağlantı isteyebilirsin.',
          style: theme.textTheme.bodySmall,
          textAlign: TextAlign.center,
        ),
        const SizedBox(height: 24),
        OutlinedButton(onPressed: _toLogin, child: const Text('Giriş ekranına git')),
      ],
    );
  }
}

class _ErrorBox extends StatelessWidget {
  final String message;
  const _ErrorBox({required this.message});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: theme.colorScheme.errorContainer,
        borderRadius: BorderRadius.circular(AppRadius.control),
      ),
      child: Row(
        children: [
          Icon(Icons.error_outline, size: 18, color: theme.colorScheme.error),
          const SizedBox(width: 10),
          Expanded(
            child: Text(message,
                style: TextStyle(color: theme.colorScheme.error, fontSize: 13)),
          ),
        ],
      ),
    );
  }
}
