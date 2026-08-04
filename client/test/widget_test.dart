import 'package:flutter_test/flutter_test.dart';

import 'package:veriyonetim_client/api_service.dart';
import 'package:veriyonetim_client/main.dart';

void main() {
  // İskelet smoke test: oturum yoksa giriş ekranı gelmeli.
  testWidgets('Oturum yoksa giriş ekranı gösterilir', (WidgetTester tester) async {
    await tester.pumpWidget(const VeriYonetimApp(session: SessionState.signedOut));

    expect(find.text('Tekrar hoş geldin'), findsOneWidget);
    expect(find.text('Giriş yap'), findsOneWidget);
    expect(find.text('Firmanı kaydet'), findsOneWidget);
  });

  // Sunucuya ulaşılamadığında giriş ekranı GÖSTERİLMEMELİ: oturum bitmedi, sadece
  // sunucu yok. Giriş ekranı kullanıcıya yanlış bilgi verir ve şifresini boşuna girdirir.
  testWidgets('Sunucuya ulaşılamıyorsa oturum kapatılmış gibi gösterilmez',
      (WidgetTester tester) async {
    await tester.pumpWidget(const VeriYonetimApp(session: SessionState.serverUnreachable));

    expect(find.text('Sunucuya ulaşılamıyor'), findsOneWidget);
    expect(find.text('Yeniden dene'), findsOneWidget);
    expect(find.text('Tekrar hoş geldin'), findsNothing);
  });
}
