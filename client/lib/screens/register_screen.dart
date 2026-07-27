import 'package:flutter/material.dart';
import '../api_service.dart';
import '../widgets/ui.dart';
import 'home_shell.dart';
import 'login_screen.dart';

// Kayıt formu: tenant (firma) + admin kullanıcı birlikte açılır. Başarılı kayıt
// doğrudan token döndürdüğünden, kayıt sonrası kullanıcı giriş yapmış sayılır.
class RegisterScreen extends StatefulWidget {
  const RegisterScreen({super.key});

  @override
  State<RegisterScreen> createState() => _RegisterScreenState();
}

class _RegisterScreenState extends State<RegisterScreen> {
  final _tenantName = TextEditingController();
  final _email = TextEditingController();
  final _password = TextEditingController();
  bool _loading = false;
  bool _obscure = true;
  String? _error;

  Future<void> _submit() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      await ApiService.register(
        tenantName: _tenantName.text.trim(),
        email: _email.text.trim(),
        password: _password.text,
        rememberMe: true, // yeni hesap → kendi cihazı varsayımıyla oturumu açık tut
      );
      if (!mounted) return;
      Navigator.pushAndRemoveUntil(
        context,
        MaterialPageRoute(builder: (_) => const HomeShell()),
        (route) => false, // geçmişi temizle: geri tuşu giriş ekranına dönmesin
      );
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  void dispose() {
    _tenantName.dispose();
    _email.dispose();
    _password.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: AuthLayout(
        child: AuthCard(
          title: 'Firmanı kaydet',
          subtitle:
              'Firmanı açan ilk kullanıcı yönetici olur; ekibini sonra sen eklersin.',
          error: _error,
          children: [
            TextField(
              controller: _tenantName,
              textInputAction: TextInputAction.next,
              decoration: const InputDecoration(
                labelText: 'Firma adı',
                helperText: 'Verilerin bu firmaya bağlı tutulur',
                prefixIcon: Icon(Icons.apartment_outlined, size: 18),
              ),
            ),
            const SizedBox(height: 18),
            TextField(
              controller: _email,
              keyboardType: TextInputType.emailAddress,
              textInputAction: TextInputAction.next,
              decoration: const InputDecoration(
                labelText: 'E-posta',
                prefixIcon: Icon(Icons.alternate_email, size: 18),
              ),
            ),
            const SizedBox(height: 18),
            TextField(
              controller: _password,
              obscureText: _obscure,
              onSubmitted: (_) => _loading ? null : _submit(),
              decoration: InputDecoration(
                labelText: 'Şifre',
                helperText: 'En az 8 karakter',
                prefixIcon: const Icon(Icons.lock_outline, size: 18),
                suffixIcon: IconButton(
                  onPressed: () => setState(() => _obscure = !_obscure),
                  icon: Icon(
                      _obscure
                          ? Icons.visibility_outlined
                          : Icons.visibility_off_outlined,
                      size: 18),
                  tooltip: _obscure ? 'Şifreyi göster' : 'Şifreyi gizle',
                ),
              ),
            ),
            const SizedBox(height: 24),
            SizedBox(
              width: double.infinity,
              child: FilledButton(
                onPressed: _loading ? null : _submit,
                child: _loading
                    ? const ButtonSpinner()
                    : const Text('Firmayı oluştur'),
              ),
            ),
            const SizedBox(height: 8),
            Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Text('Zaten hesabın var mı?',
                    style: Theme.of(context).textTheme.bodySmall),
                TextButton(
                  onPressed: () => Navigator.pop(context),
                  child: const Text('Giriş yap'),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
