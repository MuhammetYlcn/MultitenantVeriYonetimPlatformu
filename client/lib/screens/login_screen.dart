import 'package:flutter/material.dart';
import '../api_service.dart';
import '../theme/app_theme.dart';
import '../widgets/ui.dart';
import 'home_shell.dart';
import 'register_screen.dart';

// Giriş ekranı. Geniş ekranda solda markayı anlatan bir pano, sağda form;
// dar ekranda yalnız form (pano gizlenir).
class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  // TextField'ları okumanın yolu: controller (C# TextBox.Text'e bağlanmak gibi).
  final _email = TextEditingController();
  final _password = TextEditingController();
  bool _loading = false;
  bool _rememberMe = true; // "oturumu açık tut" — varsayılan açık (kendi cihazı varsayımı)
  bool _obscure = true;
  String? _error;

  // async/await burada C#'takiyle birebir aynı; ApiService.login bir Future döndürür.
  Future<void> _submit() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      await ApiService.login(_email.text.trim(), _password.text,
          rememberMe: _rememberMe);
      if (!mounted) return; // async sonrası widget hâlâ ekranda mı?
      Navigator.pushReplacement(
        context,
        MaterialPageRoute(builder: (_) => const HomeShell()),
      );
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  void dispose() {
    _email.dispose();
    _password.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: AuthLayout(
        child: AuthCard(
          title: 'Tekrar hoş geldin',
          subtitle: 'Firmanın verilerine erişmek için giriş yap.',
          error: _error,
          children: [
            TextField(
              controller: _email,
              keyboardType: TextInputType.emailAddress,
              textInputAction: TextInputAction.next,
              decoration: const InputDecoration(
                labelText: 'E-posta',
                prefixIcon: Icon(Icons.alternate_email, size: 18),
              ),
            ),
            const SizedBox(height: 14),
            TextField(
              controller: _password,
              obscureText: _obscure,
              onSubmitted: (_) => _loading ? null : _submit(),
              decoration: InputDecoration(
                labelText: 'Şifre',
                prefixIcon: const Icon(Icons.lock_outline, size: 18),
                suffixIcon: IconButton(
                  onPressed: () => setState(() => _obscure = !_obscure),
                  icon: Icon(
                      _obscure ? Icons.visibility_outlined : Icons.visibility_off_outlined,
                      size: 18),
                  tooltip: _obscure ? 'Şifreyi göster' : 'Şifreyi gizle',
                ),
              ),
            ),
            const SizedBox(height: 6),
            CheckboxListTile(
              value: _rememberMe,
              onChanged: (v) => setState(() => _rememberMe = v ?? false),
              title: const Text('Oturumu açık tut'),
              subtitle: const Text('Tarayıcıyı kapatsan da girişin korunur'),
              controlAffinity: ListTileControlAffinity.leading,
              contentPadding: EdgeInsets.zero,
              dense: true,
            ),
            const SizedBox(height: 18),
            SizedBox(
              width: double.infinity,
              child: FilledButton(
                onPressed: _loading ? null : _submit,
                child: _loading ? const ButtonSpinner() : const Text('Giriş yap'),
              ),
            ),
            const SizedBox(height: 8),
            Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Text('Hesabın yok mu?',
                    style: Theme.of(context).textTheme.bodySmall),
                TextButton(
                  onPressed: () => Navigator.push(
                    context,
                    MaterialPageRoute(builder: (_) => const RegisterScreen()),
                  ),
                  child: const Text('Firmanı kaydet'),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

/// Giriş ve kayıt ekranlarının ortak iskeleti: solda tanıtım panosu, sağda form.
class AuthLayout extends StatelessWidget {
  final Widget child;
  const AuthLayout({super.key, required this.child});

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, c) {
        final wide = c.maxWidth >= 940;
        final form = Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(28),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: child,
            ),
          ),
        );
        if (!wide) return form;
        return Row(
          children: [
            const Expanded(child: _BrandPanel()),
            Expanded(child: form),
          ],
        );
      },
    );
  }
}

// Sol tanıtım panosu: degrade zemin + ürünün üç cümlelik vaadi.
class _BrandPanel extends StatelessWidget {
  const _BrandPanel();

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;
    return Container(
      decoration: BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: [
            AppColors.brand.withValues(alpha: 0.28),
            AppColors.bg,
            AppColors.accent.withValues(alpha: 0.16),
          ],
        ),
      ),
      child: Center(
        child: Padding(
          padding: const EdgeInsets.all(56),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 460),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    const BrandMark(size: 44),
                    const SizedBox(width: 14),
                    Text('VeriYönetim', style: t.headlineSmall),
                  ],
                ),
                const SizedBox(height: 32),
                Text(
                  'Verini yükle, anlamlandır,\nkurum dışına çıkarma.',
                  style: t.displaySmall?.copyWith(height: 1.25),
                ),
                const SizedBox(height: 20),
                Text(
                  'CSV ve Excel dosyalarını yükle; kolonlar ve tipleri otomatik algılansın, '
                  'özet ve grafiklerle anında incele.',
                  style: t.bodyLarge?.copyWith(color: AppColors.muted),
                ),
                const SizedBox(height: 36),
                const _Feature(
                  icon: Icons.lock_outline,
                  title: 'Firmalar birbirinin verisini göremez',
                  text: 'Her istek, giriş yapan firmanın verisiyle sınırlanır.',
                ),
                const _Feature(
                  icon: Icons.badge_outlined,
                  title: 'Üç kademeli yetki',
                  text: 'İzleyici görür, editör veri yazar, yönetici ekip kurar.',
                ),
                const _Feature(
                  icon: Icons.insights_outlined,
                  title: 'Hazır özet ve grafikler',
                  text: 'Yüklenen her veri seti için otomatik pano.',
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _Feature extends StatelessWidget {
  final IconData icon;
  final String title;
  final String text;

  const _Feature({required this.icon, required this.title, required this.text});

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;
    return Padding(
      padding: const EdgeInsets.only(bottom: 18),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          IconBadge(icon: icon, color: AppColors.accent, size: 34),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: t.titleSmall),
                const SizedBox(height: 2),
                Text(text, style: t.bodySmall),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// Form kartı: başlık, açıklama, hata kutusu ve alanlar.
class AuthCard extends StatelessWidget {
  final String title;
  final String subtitle;
  final String? error;
  final List<Widget> children;

  const AuthCard({
    super.key,
    required this.title,
    required this.subtitle,
    required this.children,
    this.error,
  });

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(28),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            // Dar ekranda sol pano gizlendiği için marka burada görünür.
            LayoutBuilder(
              builder: (context, c) => MediaQuery.of(context).size.width < 940
                  ? Padding(
                      padding: const EdgeInsets.only(bottom: 20),
                      child: Row(
                        children: [
                          const BrandMark(size: 34),
                          const SizedBox(width: 12),
                          Text('VeriYönetim', style: t.titleLarge),
                        ],
                      ),
                    )
                  : const SizedBox.shrink(),
            ),
            Text(title, style: t.headlineSmall),
            const SizedBox(height: 6),
            Text(subtitle, style: t.bodySmall),
            const SizedBox(height: 24),
            if (error != null) ...[
              _ErrorBox(message: error!),
              const SizedBox(height: 16),
            ],
            ...children,
          ],
        ),
      ),
    );
  }
}

// Form hataları: kırmızı düz metin yerine ayırt edilebilir bir kutu.
class _ErrorBox extends StatelessWidget {
  final String message;
  const _ErrorBox({required this.message});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      decoration: BoxDecoration(
        color: AppColors.danger.withValues(alpha: 0.10),
        border: Border.all(color: AppColors.danger.withValues(alpha: 0.4)),
        borderRadius: BorderRadius.circular(AppRadius.control),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(Icons.error_outline, size: 18, color: AppColors.danger),
          const SizedBox(width: 10),
          Expanded(
            child: Text(message,
                style: const TextStyle(color: AppColors.danger, fontSize: 13)),
          ),
        ],
      ),
    );
  }
}
