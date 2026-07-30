import 'package:flutter_test/flutter_test.dart';

import 'package:veriyonetim_admin/main.dart';

void main() {
  // Smoke test: panel açılışında işletmeci giriş ekranı gelmeli ve KAYIT bağlantısı
  // OLMAMALI — platform kimliği self-servis oluşturulamaz, yalnızca sunucu ayarından
  // tohumlanır (müşteri uygulamasındaki "Firmanı kaydet" karşılığı burada yok).
  testWidgets('Açılışta platform giriş ekranı gösterilir', (WidgetTester tester) async {
    await tester.pumpWidget(const PlatformAdminApp());

    expect(find.text('Platform Paneli'), findsOneWidget);
    expect(find.text('İşletmeci girişi'), findsOneWidget);
    expect(find.text('Giriş yap'), findsOneWidget);
    expect(find.textContaining('kaydet'), findsNothing);
  });
}
