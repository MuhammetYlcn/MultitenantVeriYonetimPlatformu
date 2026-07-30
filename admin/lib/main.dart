import 'package:flutter/material.dart';
import 'platform_api.dart';
import 'screens/platform_home_screen.dart';
import 'screens/platform_login_screen.dart';
import 'theme/app_theme.dart';

// Platform panelinin girişi. Müşteri uygulamasından (client/) AYRI bir Flutter
// projesidir ve ayrı derlenir: panelin kodu müşteri tarayıcısına hiç inmez,
// ayrı port/host üzerinden servis edildiği için ağ seviyesinde de kısıtlanabilir.
void main() {
  // Depodaki token'ı runApp'tan önce oku: oturum sürüyorsa doğrudan panele girilsin.
  PlatformApi.loadSession();
  runApp(const PlatformAdminApp());
}

class PlatformAdminApp extends StatelessWidget {
  const PlatformAdminApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'VeriYönetim — Platform Paneli',
      debugShowCheckedModeBanner: false,
      // Tek tema: koyu. Vurgu rengi müşteri uygulamasından farklı (amber) —
      // işletmeci hangi yüzeyde olduğunu bir bakışta görsün.
      theme: AppTheme.dark,
      home: PlatformApi.isLoggedIn
          ? const PlatformHomeScreen()
          : const PlatformLoginScreen(),
    );
  }
}
