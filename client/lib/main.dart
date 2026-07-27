import 'package:flutter/material.dart';
import 'api_service.dart';
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

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'VeriYönetim',
      debugShowCheckedModeBanner: false,
      // Uygulama tek temalı: koyu. Tüm renk/tipografi kararları theme/app_theme.dart'ta.
      theme: AppTheme.dark,
      // Açılışta token yüklüyse oturum sürüyor demektir → doğrudan kabuk; yoksa giriş.
      home: ApiService.isLoggedIn ? const HomeShell() : const LoginScreen(),
    );
  }
}
