import 'package:flutter/material.dart';
import '../platform_api.dart';
import '../theme/app_theme.dart';
import 'platform_home_screen.dart';

/// Platform yöneticisi girişi. Müşteri giriş ekranından farkı: KAYIT BAĞLANTISI YOK
/// — platform kimliği self-servis oluşturulamaz, yalnızca sunucu ayarından tohumlanır.
class PlatformLoginScreen extends StatefulWidget {
  const PlatformLoginScreen({super.key});

  @override
  State<PlatformLoginScreen> createState() => _PlatformLoginScreenState();
}

class _PlatformLoginScreenState extends State<PlatformLoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _emailController = TextEditingController();
  final _passwordController = TextEditingController();
  bool _busy = false;
  bool _obscure = true;
  String? _error;

  @override
  void dispose() {
    _emailController.dispose();
    _passwordController.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      await PlatformApi.login(
          _emailController.text.trim(), _passwordController.text);
      if (!mounted) return;
      Navigator.of(context).pushReplacement(
          MaterialPageRoute(builder: (_) => const PlatformHomeScreen()));
    } on Object catch (e) {
      if (!mounted) return;
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      body: Center(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 420),
            child: Card(
              child: Padding(
                padding: const EdgeInsets.all(32),
                child: Form(
                  key: _formKey,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Row(
                        children: [
                          Container(
                            padding: const EdgeInsets.all(10),
                            decoration: BoxDecoration(
                              color: AppColors.brand.withValues(alpha: 0.15),
                              borderRadius:
                                  BorderRadius.circular(AppRadius.small),
                            ),
                            child: const Icon(Icons.shield_outlined,
                                color: AppColors.brand),
                          ),
                          const SizedBox(width: 14),
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text('Platform Paneli',
                                    style: theme.textTheme.titleLarge),
                                Text('İşletmeci girişi',
                                    style: theme.textTheme.bodySmall),
                              ],
                            ),
                          ),
                        ],
                      ),
                      const SizedBox(height: 8),
                      const Divider(),
                      const SizedBox(height: 20),

                      // Panelin sınırını kullanıcıya da açıkça söyle.
                      Container(
                        padding: const EdgeInsets.all(14),
                        decoration: BoxDecoration(
                          color: AppColors.surfaceAlt,
                          borderRadius: BorderRadius.circular(AppRadius.control),
                          border: Border.all(color: AppColors.border),
                        ),
                        child: Row(
                          children: [
                            const Icon(Icons.lock_outline,
                                size: 18, color: AppColors.muted),
                            const SizedBox(width: 10),
                            Expanded(
                              child: Text(
                                'Bu panel firmaları yönetir. Firmaların verisine '
                                'erişimi yoktur.',
                                style: theme.textTheme.bodySmall,
                              ),
                            ),
                          ],
                        ),
                      ),
                      const SizedBox(height: 20),

                      TextFormField(
                        controller: _emailController,
                        decoration: const InputDecoration(labelText: 'E-posta'),
                        keyboardType: TextInputType.emailAddress,
                        autofillHints: const [AutofillHints.email],
                        validator: (v) => (v == null || v.trim().isEmpty)
                            ? 'E-posta gerekli.'
                            : null,
                      ),
                      const SizedBox(height: 14),
                      TextFormField(
                        controller: _passwordController,
                        obscureText: _obscure,
                        decoration: InputDecoration(
                          labelText: 'Şifre',
                          suffixIcon: IconButton(
                            icon: Icon(_obscure
                                ? Icons.visibility_outlined
                                : Icons.visibility_off_outlined),
                            onPressed: () =>
                                setState(() => _obscure = !_obscure),
                          ),
                        ),
                        onFieldSubmitted: (_) => _submit(),
                        validator: (v) =>
                            (v == null || v.isEmpty) ? 'Şifre gerekli.' : null,
                      ),

                      if (_error != null) ...[
                        const SizedBox(height: 16),
                        Container(
                          padding: const EdgeInsets.all(12),
                          decoration: BoxDecoration(
                            color: theme.colorScheme.errorContainer,
                            borderRadius:
                                BorderRadius.circular(AppRadius.control),
                          ),
                          child: Row(
                            children: [
                              Icon(Icons.error_outline,
                                  size: 18, color: theme.colorScheme.error),
                              const SizedBox(width: 10),
                              Expanded(
                                child: Text(_error!,
                                    style: TextStyle(
                                        color: theme.colorScheme.error,
                                        fontSize: 13)),
                              ),
                            ],
                          ),
                        ),
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
                            : const Text('Giriş yap'),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
