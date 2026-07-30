import 'package:flutter/material.dart';
import 'api_service.dart';
import 'screens/accept_invitation_screen.dart';
import 'screens/home_shell.dart';
import 'screens/login_screen.dart';
import 'theme/app_theme.dart';

// Uygulama girişi. runApp'tan ÖNCE kalıcı depodaki token'ı okuyoruz ki açılış ekranına
// doğru karar verebilelim: token varsa oturum sürüyordur → doğrudan uygulama kabuğu.
Future<void> main() async {
  // runApp'tan önce asenkron iş (depo okuma) yapacağımız için Flutter altyapısını
  // elle başlatmamız gerekir; yoksa "binding not initialized" hatası alınır.
  WidgetsFlutterBinding.ensureInitialized();
  await ApiService.loadToken();
  runApp(const VeriYonetimApp());
}

// Durumsuz kök widget: sadece MaterialApp'i kurar (tema + başlangıç ekranı).
class VeriYonetimApp extends StatelessWidget {
  const VeriYonetimApp({super.key});

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
  Widget build(BuildContext context) {
    // Davet/şifre sıfırlama bağlantısı bir ADRESLE gelir (#/davet/<token>), bu yüzden
    // açılışta adrese bakmak gerekir. Bu ekran oturum GEREKTİRMEZ: davet edilen kişinin
    // henüz hesabı yoktur, şifresini unutan da giriş yapamaz.
    final inviteToken = _inviteTokenFromUrl();

    return MaterialApp(
      title: 'VeriYönetim',
      debugShowCheckedModeBanner: false,
      // Uygulama tek temalı: koyu. Tüm renk/tipografi kararları theme/app_theme.dart'ta.
      theme: AppTheme.dark,
      home: inviteToken != null
          ? AcceptInvitationScreen(token: inviteToken)
          // Açılışta token yüklüyse oturum sürüyor demektir → doğrudan kabuk; yoksa giriş.
          : (ApiService.isLoggedIn ? const HomeShell() : const LoginScreen()),
    );
  }
}
