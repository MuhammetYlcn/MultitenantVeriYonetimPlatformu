import 'package:flutter_test/flutter_test.dart';

import 'package:veriyonetim_client/main.dart';

void main() {
  // İskelet smoke test: uygulama açılışında giriş ekranı gelmeli.
  testWidgets('Açılışta giriş ekranı gösterilir', (WidgetTester tester) async {
    await tester.pumpWidget(const VeriYonetimApp());

    expect(find.text('Tekrar hoş geldin'), findsOneWidget);
    expect(find.text('Giriş yap'), findsOneWidget);
    expect(find.text('Firmanı kaydet'), findsOneWidget);
  });
}
