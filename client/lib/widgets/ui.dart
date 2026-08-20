import 'package:flutter/material.dart';
import '../api_service.dart';
import '../theme/app_theme.dart';

// Ekranlarda tekrar eden parçalar burada tek kez tanımlanır: sayfa başlığı, yükleniyor,
// hata, boş durum, KPI kartı, bölüm kartı, rol rozeti. Önceden her ekran kendi
// "CircularProgressIndicator" / kırmızı hata metnini yazıyordu; artık hepsi aynı görünür.

/// Sayfanın üst bloğu: iri başlık + açıklama satırı + sağda eylem butonları.
/// Butonlar artık ekranın sağ altındaki yuvarlak FAB yerine burada duruyor
/// (masaüstü/web arayüzlerinin alışılmış yeri).
class PageHeader extends StatelessWidget {
  final String title;
  final String? subtitle;
  final List<Widget> actions;
  final Widget? leading;

  const PageHeader({
    super.key,
    required this.title,
    this.subtitle,
    this.actions = const [],
    this.leading,
  });

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;
    return Padding(
      padding: const EdgeInsets.only(bottom: 20),
      child: Wrap(
        alignment: WrapAlignment.spaceBetween,
        crossAxisAlignment: WrapCrossAlignment.center,
        spacing: 16,
        runSpacing: 12,
        children: [
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (leading != null) ...[leading!, const SizedBox(width: 8)],
              Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(title, style: t.headlineSmall),
                  if (subtitle != null) ...[
                    const SizedBox(height: 4),
                    Text(subtitle!, style: t.bodySmall),
                  ],
                ],
              ),
            ],
          ),
          if (actions.isNotEmpty)
            Wrap(spacing: 10, runSpacing: 8, children: actions),
        ],
      ),
    );
  }
}

/// Başlık + isteğe bağlı sağ üst köşe öğesi taşıyan içerik kartı (grafik, tablo vb.).
class SectionCard extends StatelessWidget {
  final String? title;
  final String? subtitle;
  final Widget? trailing;
  final Widget child;
  final EdgeInsetsGeometry padding;

  const SectionCard({
    super.key,
    this.title,
    this.subtitle,
    this.trailing,
    required this.child,
    this.padding = const EdgeInsets.all(20),
  });

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;
    return Card(
      child: Padding(
        padding: padding,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (title != null) ...[
              Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(title!, style: t.titleMedium),
                        if (subtitle != null) ...[
                          const SizedBox(height: 2),
                          Text(subtitle!, style: t.bodySmall),
                        ],
                      ],
                    ),
                  ),
                  ?trailing,
                ],
              ),
              const SizedBox(height: 18),
            ],
            child,
          ],
        ),
      ),
    );
  }
}

/// Tek bir sayıyı öne çıkaran özet kartı (satır sayısı, toplam, ortalama…).
class StatTile extends StatelessWidget {
  final String label;
  final String value;
  final String? hint;
  final IconData icon;
  final Color color;

  const StatTile({
    super.key,
    required this.label,
    required this.value,
    this.hint,
    this.icon = Icons.insights_outlined,
    this.color = AppColors.brand,
  });

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(label.toUpperCase(),
                      style: t.labelSmall,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis),
                ),
                IconBadge(icon: icon, color: color, size: 30),
              ],
            ),
            const SizedBox(height: 12),
            Text(value,
                style: t.headlineMedium, maxLines: 1, overflow: TextOverflow.ellipsis),
            if (hint != null) ...[
              const SizedBox(height: 4),
              Text(hint!, style: t.bodySmall?.copyWith(color: color)),
            ],
          ],
        ),
      ),
    );
  }
}

/// Renkli, yuvarlatılmış simge kutusu — liste ve kartlarda görsel çapa olarak kullanılır.
class IconBadge extends StatelessWidget {
  final IconData icon;
  final Color color;
  final double size;

  const IconBadge({
    super.key,
    required this.icon,
    this.color = AppColors.accent,
    this.size = 40,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      width: size,
      height: size,
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.16),
        borderRadius: BorderRadius.circular(AppRadius.small),
      ),
      child: Icon(icon, color: color, size: size * 0.5),
    );
  }
}

/// Uygulama markası: degrade kare + ad. Sol menüde ve giriş ekranında kullanılır.
class BrandMark extends StatelessWidget {
  final double size;
  const BrandMark({super.key, this.size = 36});

  @override
  Widget build(BuildContext context) {
    return Container(
      width: size,
      height: size,
      decoration: BoxDecoration(
        gradient: const LinearGradient(
          colors: [AppColors.brand, AppColors.accent],
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
        ),
        borderRadius: BorderRadius.circular(size * 0.32),
      ),
      child: Icon(Icons.dataset_outlined, color: Colors.white, size: size * 0.55),
    );
  }
}

/// Rolü renkle ayırt eden küçük rozet (İzleyici / Editör / Yönetici).
class RoleBadge extends StatelessWidget {
  final String role;
  final bool compact;

  const RoleBadge({super.key, required this.role, this.compact = false});

  static Color colorOf(String role) => switch (role) {
        'Admin' => AppColors.brand,
        'Editor' => AppColors.accent,
        _ => AppColors.muted,
      };

  @override
  Widget build(BuildContext context) {
    final color = colorOf(role);
    return Container(
      padding: EdgeInsets.symmetric(horizontal: compact ? 10 : 12, vertical: 5),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.14),
        border: Border.all(color: color.withValues(alpha: 0.45)),
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        roleLabels[role] ?? role,
        style: TextStyle(
            color: color,
            fontSize: compact ? 11.5 : 12.5,
            fontWeight: FontWeight.w600),
      ),
    );
  }
}

/// Veri beklenirken gösterilen ortak görünüm.
class LoadingView extends StatelessWidget {
  final String? message;
  const LoadingView({super.key, this.message});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const SizedBox(
              width: 28, height: 28, child: CircularProgressIndicator(strokeWidth: 2.5)),
          if (message != null) ...[
            const SizedBox(height: 16),
            Text(message!, style: Theme.of(context).textTheme.bodySmall),
          ],
        ],
      ),
    );
  }
}

/// İstek başarısız olduğunda: sebep + "Tekrar dene" düğmesi.
class ErrorView extends StatelessWidget {
  final String message;
  final VoidCallback? onRetry;

  const ErrorView({super.key, required this.message, this.onRetry});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: Card(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                const IconBadge(
                    icon: Icons.error_outline, color: AppColors.danger, size: 44),
                const SizedBox(height: 16),
                Text('Bir şeyler ters gitti',
                    style: Theme.of(context).textTheme.titleMedium),
                const SizedBox(height: 8),
                Text(message,
                    textAlign: TextAlign.center,
                    style: Theme.of(context).textTheme.bodySmall),
                if (onRetry != null) ...[
                  const SizedBox(height: 20),
                  OutlinedButton.icon(
                    onPressed: onRetry,
                    icon: const Icon(Icons.refresh, size: 18),
                    label: const Text('Tekrar dene'),
                  ),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Liste boşken gösterilen yönlendirici görünüm (ne olduğu + ne yapılabileceği).
class EmptyState extends StatelessWidget {
  final IconData icon;
  final String title;
  final String? message;
  final Widget? action;

  const EmptyState({
    super.key,
    required this.icon,
    required this.title,
    this.message,
    this.action,
  });

  @override
  Widget build(BuildContext context) {
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 460),
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              IconBadge(icon: icon, color: AppColors.brand, size: 56),
              const SizedBox(height: 20),
              Text(title,
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.titleLarge),
              if (message != null) ...[
                const SizedBox(height: 8),
                Text(message!,
                    textAlign: TextAlign.center,
                    style: Theme.of(context).textTheme.bodySmall),
              ],
              if (action != null) ...[const SizedBox(height: 22), action!],
            ],
          ),
        ),
      ),
    );
  }
}

/// Buton içinde işlem sürerken dönen küçük gösterge (buton boyu sabit kalsın diye).
class ButtonSpinner extends StatelessWidget {
  const ButtonSpinner({super.key});

  @override
  Widget build(BuildContext context) => const SizedBox(
        width: 18,
        height: 18,
        child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white),
      );
}

/// Ekranın her yerinden aynı biçimde bildirim göstermek için tek yardımcı.
void showSnack(BuildContext context, String message, {bool isError = false}) {
  ScaffoldMessenger.of(context).showSnackBar(
    SnackBar(
      content: Row(
        children: [
          Icon(isError ? Icons.error_outline : Icons.check_circle_outline,
              size: 18, color: isError ? AppColors.danger : AppColors.accent),
          const SizedBox(width: 10),
          Expanded(child: Text(message)),
        ],
      ),
    ),
  );
}

/// "3 saat önce" gibi göreli zaman: listelerde tam tarih gereksiz gürültü yaratır.
/// Bir aydan eskisi için tarihe döner — "47 gün önce" kimsenin kafasında bir güne
/// karşılık gelmiyor.
String timeAgo(DateTime time) {
  final local = time.toLocal();
  final diff = DateTime.now().difference(local);
  if (diff.isNegative) return 'az sonra';
  if (diff.inMinutes < 1) return 'az önce';
  if (diff.inMinutes < 60) return '${diff.inMinutes} dakika önce';
  if (diff.inHours < 24) return '${diff.inHours} saat önce';
  if (diff.inDays < 30) return '${diff.inDays} gün önce';
  return '${local.day}.${local.month}.${local.year}';
}

/// "12 dakika sonra" — izleyicinin sıradaki koşusu gibi GELECEK bir an için.
String timeUntil(DateTime time) {
  final diff = time.toLocal().difference(DateTime.now());
  if (diff.isNegative) return 'birazdan';
  if (diff.inMinutes < 1) return 'birazdan';
  if (diff.inMinutes < 60) return '${diff.inMinutes} dakika sonra';
  if (diff.inHours < 24) return '${diff.inHours} saat sonra';
  return '${diff.inDays} gün sonra';
}
