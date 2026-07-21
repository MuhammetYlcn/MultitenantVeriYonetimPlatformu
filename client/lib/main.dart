import 'package:flutter/material.dart';
import 'screens/login_screen.dart';

// Uygulama girişi. Java'daki main() + Spring Boot @SpringBootApplication karşılığı;
// runApp kök widget'ı ekrana bağlar.
void main() => runApp(const VeriYonetimApp());

// Durumsuz kök widget: sadece MaterialApp'i kurar (tema + başlangıç ekranı).
class VeriYonetimApp extends StatelessWidget {
  const VeriYonetimApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'VeriYönetim',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        colorSchemeSeed: Colors.indigo,
        useMaterial3: true,
      ),
      home: const LoginScreen(), // açılış ekranı: giriş
    );
  }
}
