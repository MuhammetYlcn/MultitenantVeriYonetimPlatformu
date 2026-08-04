import 'package:flutter/material.dart';
import 'api_service.dart';
import 'screens/accept_invitation_screen.dart';
import 'screens/home_shell.dart';
import 'screens/login_screen.dart';
import 'theme/app_theme.dart';
import 'widgets/ui.dart';

// Uygulama girişi. runApp'tan ÖNCE kalıcı depodaki token'ı okuyoruz ki açılış ekranına
// doğru karar verebilelim: token varsa oturum sürüyordur → doğrudan uygulama kabuğu.
Future<void> main() async {
  // runApp'tan önce asenkron iş (depo okuma) yapacağımız için Flutter altyapısını
  // elle başlatmamız gerekir; yoksa "binding not initialized" hatası alınır.
  WidgetsFlutterBinding.ensureInitialized();
  final session = await ApiService.loadToken();
  runApp(VeriYonetimApp(session: session));
}

class VeriYonetimApp extends StatefulWidget {
  final SessionState session;

  const VeriYonetimApp({super.key, required this.session});

  /// Adres `…/#/davet/<token>` ise token'ı çıkarır, değilse null.
  /// Flutter web varsayılan olarak hash yönlendirme kullandığından yol Uri.base'in
  /// fragment kısmında durur.
  static String? _inviteTokenFromUrl() {
    final path = Uri.base.fragment; // ör. "/davet/AbC123"
    const prefix = '/davet/';
    if (!path.startsWith(prefix)) return null;

    final token = path.substring(prefix.length).trim();
    return token.isEmpty ? null : token;
  }

  @override
  State<VeriYonetimApp> createState() => _VeriYonetimAppState();
}

class _VeriYonetimAppState extends State<VeriYonetimApp> {
  late SessionState _session = widget.session;

  // Sunucu geri geldiğinde yeniden dener. Şifre sorulmaz: oturum zaten duruyor,
  // eksik olan tek şey sunucuya erişimdi.
  Future<void> _retry() async {
    final session = await ApiService.loadToken();
    if (!mounted) return;
    setState(() => _session = session);
  }

  @override
  Widget build(BuildContext context) {
    // Davet/şifre sıfırlama bağlantısı bir ADRESLE gelir (#/davet/<token>), bu yüzden
    // açılışta adrese bakmak gerekir. Bu ekran oturum GEREKTİRMEZ: davet edilen kişinin
    // henüz hesabı yoktur, şifresini unutan da giriş yapamaz.
    final inviteToken = VeriYonetimApp._inviteTokenFromUrl();

    return MaterialApp(
      title: 'VeriYönetim',
      debugShowCheckedModeBanner: false,
      // Uygulama tek temalı: koyu. Tüm renk/tipografi kararları theme/app_theme.dart'ta.
      theme: AppTheme.dark,
      home: inviteToken != null
          ? AcceptInvitationScreen(token: inviteToken)
          : switch (_session) {
              SessionState.signedIn => const HomeShell(),
              // Sunucuya ulaşılamıyor: oturum BİTMEDİ. Giriş ekranı göstermek
              // kullanıcıya yanlış bilgi verir ve şifresini boşuna girdirir.
              SessionState.serverUnreachable => _ServerUnreachableScreen(onRetry: _retry),
              SessionState.signedOut => const LoginScreen(),
            },
    );
  }
}

/// Sunucuya ulaşılamadığında gösterilir. Oturum korunur; kullanıcı yalnızca bekler.
class _ServerUnreachableScreen extends StatefulWidget {
  final Future<void> Function() onRetry;

  const _ServerUnreachableScreen({required this.onRetry});

  @override
  State<_ServerUnreachableScreen> createState() => _ServerUnreachableScreenState();
}

class _ServerUnreachableScreenState extends State<_ServerUnreachableScreen> {
  bool _trying = false;

  Future<void> _retry() async {
    setState(() => _trying = true);
    await widget.onRetry();
    if (mounted) setState(() => _trying = false);
  }

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;

    return Scaffold(
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 420),
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 58,
                  height: 58,
                  decoration: BoxDecoration(
                    color: AppColors.warning.withValues(alpha: 0.14),
                    borderRadius: BorderRadius.circular(18),
                  ),
                  child: const Icon(Icons.cloud_off_outlined,
                      color: AppColors.warning, size: 26),
                ),
                const SizedBox(height: 20),
                Text('Sunucuya ulaşılamıyor',
                    style: t.headlineSmall, textAlign: TextAlign.center),
                const SizedBox(height: 10),
                Text(
                  'Oturumun açık, kapanmadı. Sunucu şu an yanıt vermiyor — '
                  'çalışmaya başlayınca kaldığın yerden devam edeceksin.',
                  style: t.bodySmall,
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 24),
                FilledButton.icon(
                  onPressed: _trying ? null : _retry,
                  icon: _trying
                      ? const ButtonSpinner()
                      : const Icon(Icons.refresh, size: 18),
                  label: Text(_trying ? 'Deneniyor…' : 'Yeniden dene'),
                ),
                const SizedBox(height: 12),
                TextButton(
                  onPressed: () async {
                    // Yine de çıkmak isteyen için: token'ları temizleyip giriş ekranına döner.
                    await ApiService.logout();
                    if (context.mounted) await widget.onRetry();
                  },
                  child: const Text('Oturumu kapat'),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
