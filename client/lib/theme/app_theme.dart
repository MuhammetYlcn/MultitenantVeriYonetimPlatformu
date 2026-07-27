import 'package:flutter/material.dart';

// Uygulamanın BÜTÜN görsel kararları (renk, yuvarlaklık, tipografi, bileşen stilleri)
// tek yerde toplanır. Ekranlar artık kendi renklerini seçmez; `Theme.of(context)`
// üzerinden buradaki şemayı okur. Böylece paleti değiştirmek = bu dosyayı değiştirmek.
//
// Uygulama bilinçli olarak TEK temalı: koyu. Açık tema yok — ikisini birden taşımak
// her bileşeni iki kez doğrulamayı gerektirirdi; tek tema, tek doğru görünüm.

/// Palet. Zemin → kart → kenar sırasıyla açılan üç koyu ton, üstünde iki canlı vurgu.
class AppColors {
  // Ana marka rengi: menü seçimi, birincil butonlar, bağlantılar.
  static const brand = Color(0xFF8B7CF6); // menekşe-indigo
  // İkincil vurgu: grafik çubukları, olumlu durumlar, veri seti simgeleri.
  static const accent = Color(0xFF22D3B4); // turkuaz

  static const danger = Color(0xFFFF6369);
  static const warning = Color(0xFFF5A524);

  static const bg = Color(0xFF0E1420); // sayfa zemini (kartların arkası)
  static const surface = Color(0xFF171F2E); // kart, sol menü, üst çubuk
  static const surfaceAlt = Color(0xFF1F2A3C); // tablo başlığı, giriş alanı dolgusu
  static const border = Color(0xFF2A3446); // ince ayraçlar ve kart kenarları
  static const text = Color(0xFFE7ECF4); // birincil metin
  static const muted = Color(0xFF94A3B8); // ikincil metin, etiketler
}

/// Grafik serilerinde sırayla kullanılan renkler (Adım 5'te fl_chart'a geçilirse de aynısı).
const List<Color> chartPalette = [
  Color(0xFF8B7CF6), // menekşe
  Color(0xFF22D3B4), // turkuaz
  Color(0xFF38BDF8), // gök
  Color(0xFFF5A524), // amber
  Color(0xFFEC4899), // pembe
  Color(0xFF84CC16), // yeşil
  Color(0xFFFB7185), // mercan
  Color(0xFFA78BFA), // lila
];

/// Tüm uygulamada aynı köşe yuvarlaklıkları kullanılır — dağınık görünmesin diye.
class AppRadius {
  static const card = 16.0;
  static const control = 12.0; // buton, giriş alanı
  static const small = 10.0; // simge kutusu, menü öğesi
}

class AppTheme {
  static ThemeData get dark {
    const primary = AppColors.brand;
    const secondary = AppColors.accent;

    const scheme = ColorScheme(
      brightness: Brightness.dark,
      primary: primary,
      onPrimary: Color(0xFF14091F),
      primaryContainer: Color(0xFF2A2547),
      onPrimaryContainer: primary,
      secondary: secondary,
      onSecondary: Color(0xFF06231E),
      secondaryContainer: Color(0xFF12352F),
      onSecondaryContainer: secondary,
      error: AppColors.danger,
      onError: Colors.white,
      errorContainer: Color(0xFF3A1E22),
      onErrorContainer: AppColors.danger,
      surface: AppColors.surface,
      onSurface: AppColors.text,
      // "Vurgusuz yüzey" ve "ikincil metin" rolleri: tablo başlığı, ipucu metni vb.
      surfaceContainerHighest: AppColors.surfaceAlt,
      onSurfaceVariant: AppColors.muted,
      outline: AppColors.border,
      outlineVariant: AppColors.border,
    );

    final textTheme = _textTheme();

    return ThemeData(
      useMaterial3: true,
      brightness: Brightness.dark,
      colorScheme: scheme,
      scaffoldBackgroundColor: AppColors.bg,
      canvasColor: AppColors.bg,
      textTheme: textTheme,
      // Fareyle kullanılan web arayüzü: mobilin iri dokunma alanları burada gereksiz yer kaplıyor.
      visualDensity: VisualDensity.standard,

      cardTheme: CardThemeData(
        color: AppColors.surface,
        elevation: 0,
        margin: EdgeInsets.zero,
        clipBehavior: Clip.antiAlias,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(AppRadius.card),
          side: const BorderSide(color: AppColors.border),
        ),
      ),

      dividerTheme: const DividerThemeData(
          color: AppColors.border, thickness: 1, space: 1),

      appBarTheme: AppBarTheme(
        backgroundColor: AppColors.surface,
        foregroundColor: AppColors.text,
        elevation: 0,
        scrolledUnderElevation: 0,
        centerTitle: false,
        titleTextStyle: textTheme.titleLarge,
      ),

      // Giriş alanları: sert çerçeve yerine hafif dolgu — koyu zeminde daha yumuşak durur.
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: AppColors.surfaceAlt,
        hintStyle: const TextStyle(color: AppColors.muted),
        labelStyle: const TextStyle(color: AppColors.muted),
        helperStyle: const TextStyle(color: AppColors.muted, fontSize: 12),
        contentPadding:
            const EdgeInsets.symmetric(horizontal: 16, vertical: 16),
        border: _inputBorder(AppColors.border),
        enabledBorder: _inputBorder(AppColors.border),
        focusedBorder: _inputBorder(primary, width: 1.6),
        errorBorder: _inputBorder(AppColors.danger),
        focusedErrorBorder: _inputBorder(AppColors.danger, width: 1.6),
      ),

      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: primary,
          foregroundColor: Colors.white,
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 16),
          shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(AppRadius.control)),
          textStyle: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 14),
          side: const BorderSide(color: AppColors.border),
          foregroundColor: AppColors.text,
          shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(AppRadius.control)),
          textStyle: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          foregroundColor: primary,
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 12),
          shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(AppRadius.control)),
          textStyle: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600),
        ),
      ),
      iconButtonTheme: IconButtonThemeData(
        style: IconButton.styleFrom(
          foregroundColor: AppColors.muted,
          hoverColor: primary.withValues(alpha: 0.10),
        ),
      ),

      chipTheme: const ChipThemeData(
        backgroundColor: AppColors.surfaceAlt,
        side: BorderSide(color: AppColors.border),
        labelStyle: TextStyle(
            color: AppColors.text, fontSize: 12.5, fontWeight: FontWeight.w600),
        padding: EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        shape: StadiumBorder(),
      ),

      dialogTheme: DialogThemeData(
        backgroundColor: AppColors.surface,
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(20),
          side: const BorderSide(color: AppColors.border),
        ),
        titleTextStyle: textTheme.titleLarge,
      ),

      popupMenuTheme: PopupMenuThemeData(
        color: AppColors.surface,
        elevation: 8,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(AppRadius.control),
          side: const BorderSide(color: AppColors.border),
        ),
      ),

      snackBarTheme: SnackBarThemeData(
        behavior: SnackBarBehavior.floating,
        backgroundColor: AppColors.surfaceAlt,
        contentTextStyle:
            const TextStyle(color: AppColors.text, fontSize: 13.5),
        shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(AppRadius.control)),
        insetPadding: const EdgeInsets.all(16),
      ),

      // Tablo: başlık satırı hafif dolgulu, ayraçlar ince — veri öne çıksın.
      dataTableTheme: const DataTableThemeData(
        headingRowColor: WidgetStatePropertyAll(AppColors.surfaceAlt),
        headingTextStyle: TextStyle(
          color: AppColors.muted,
          fontSize: 12.5,
          fontWeight: FontWeight.w700,
          letterSpacing: 0.4,
        ),
        dataTextStyle: TextStyle(color: AppColors.text, fontSize: 13.5),
        dividerThickness: 1,
        horizontalMargin: 20,
        columnSpacing: 28,
      ),

      // Dar ekranda sol menünün yerini alan alt gezinme çubuğu.
      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: AppColors.surface,
        indicatorColor: primary.withValues(alpha: 0.18),
        elevation: 0,
        height: 66,
        labelTextStyle: const WidgetStatePropertyAll(
          TextStyle(
              fontSize: 12, fontWeight: FontWeight.w600, color: AppColors.muted),
        ),
      ),

      progressIndicatorTheme: const ProgressIndicatorThemeData(
        color: primary,
        linearTrackColor: AppColors.surfaceAlt,
        circularTrackColor: AppColors.surfaceAlt,
      ),

      tooltipTheme: TooltipThemeData(
        decoration: BoxDecoration(
          color: AppColors.surfaceAlt,
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: AppColors.border),
        ),
        textStyle: const TextStyle(color: AppColors.text, fontSize: 12),
      ),

      checkboxTheme: CheckboxThemeData(
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(5)),
        side: const BorderSide(color: AppColors.border, width: 1.6),
      ),
    );
  }

  static OutlineInputBorder _inputBorder(Color color, {double width = 1}) =>
      OutlineInputBorder(
        borderRadius: BorderRadius.circular(AppRadius.control),
        borderSide: BorderSide(color: color, width: width),
      );

  // Tipografi: başlıklar iri ve sıkı harf aralıklı (modern arayüzlerin belirgin özelliği),
  // gövde metni okunur boyutta, ikincil metinler sönük renkte.
  static TextTheme _textTheme() => const TextTheme(
        displaySmall: TextStyle(
            fontSize: 32,
            fontWeight: FontWeight.w700,
            letterSpacing: -0.8,
            color: AppColors.text),
        headlineMedium: TextStyle(
            fontSize: 26,
            fontWeight: FontWeight.w700,
            letterSpacing: -0.6,
            color: AppColors.text),
        headlineSmall: TextStyle(
            fontSize: 22,
            fontWeight: FontWeight.w700,
            letterSpacing: -0.4,
            color: AppColors.text),
        titleLarge: TextStyle(
            fontSize: 18,
            fontWeight: FontWeight.w600,
            letterSpacing: -0.2,
            color: AppColors.text),
        titleMedium: TextStyle(
            fontSize: 15, fontWeight: FontWeight.w600, color: AppColors.text),
        titleSmall: TextStyle(
            fontSize: 13.5, fontWeight: FontWeight.w600, color: AppColors.text),
        bodyLarge: TextStyle(fontSize: 15, color: AppColors.text, height: 1.45),
        bodyMedium:
            TextStyle(fontSize: 13.5, color: AppColors.text, height: 1.45),
        bodySmall:
            TextStyle(fontSize: 12.5, color: AppColors.muted, height: 1.4),
        labelLarge: TextStyle(
            fontSize: 13.5, fontWeight: FontWeight.w600, color: AppColors.text),
        labelMedium: TextStyle(
            fontSize: 12, fontWeight: FontWeight.w600, color: AppColors.muted),
        labelSmall: TextStyle(
            fontSize: 11,
            fontWeight: FontWeight.w600,
            letterSpacing: 0.4,
            color: AppColors.muted),
      );
}
