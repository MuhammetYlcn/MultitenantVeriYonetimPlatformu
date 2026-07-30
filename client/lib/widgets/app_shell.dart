import 'package:flutter/material.dart';
import '../api_service.dart';
import '../theme/app_theme.dart';
import 'ui.dart';

// Uygulamanın kabuğu: solda kalıcı menü, sağda değişen içerik.
// Önceki tasarımda her ekran ayrı bir sayfaydı ve üst üste yığılıyordu (geri tuşuyla
// dolaşılıyordu). Artık tek bir kabuk var; menüden bölüm seçilince yalnız sağdaki
// alan değişir — masaüstü panolarının alışılmış düzeni.
//
// Dar ekranda (telefon/dar pencere) sol menü kendiliğinden alt gezinme çubuğuna döner.

/// Sol menüdeki bir bölüm.
class ShellDestination {
  final IconData icon;
  final IconData activeIcon;
  final String label;

  const ShellDestination({
    required this.icon,
    required this.activeIcon,
    required this.label,
  });
}

class AppShell extends StatelessWidget {
  final List<ShellDestination> destinations;
  final int index;
  final ValueChanged<int> onSelect;
  final VoidCallback onLogout;
  final VoidCallback onChangePassword;

  /// Üst çubukta gösterilen konum bilgisi ("Veri setleri / Satışlar 2026").
  final List<String> breadcrumb;
  final Widget child;

  const AppShell({
    super.key,
    required this.destinations,
    required this.index,
    required this.onSelect,
    required this.onLogout,
    required this.onChangePassword,
    required this.child,
    this.breadcrumb = const [],
  });

  // Bu genişliğin altında sol menü yerine alt gezinme çubuğu kullanılır.
  static const double _wideBreakpoint = 900;

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        final isWide = constraints.maxWidth >= _wideBreakpoint;
        return Scaffold(
          body: isWide
              ? Row(
                  children: [
                    _Sidebar(
                      destinations: destinations,
                      index: index,
                      onSelect: onSelect,
                      onLogout: onLogout,
                      onChangePassword: onChangePassword,
                    ),
                    Expanded(
                      child: Column(
                        children: [
                          _TopBar(
                              breadcrumb: breadcrumb,
                              showLogout: false,
                              onLogout: onLogout,
                              onChangePassword: onChangePassword),
                          Expanded(child: _content(child)),
                        ],
                      ),
                    ),
                  ],
                )
              : Column(
                  children: [
                    _TopBar(
                        breadcrumb: breadcrumb,
                        showLogout: true,
                        onLogout: onLogout,
                        onChangePassword: onChangePassword),
                    Expanded(child: _content(child)),
                  ],
                ),
          bottomNavigationBar: isWide
              ? null
              : NavigationBar(
                  selectedIndex: index,
                  onDestinationSelected: onSelect,
                  destinations: destinations
                      .map((d) => NavigationDestination(
                            icon: Icon(d.icon),
                            selectedIcon: Icon(d.activeIcon, color: AppColors.brand),
                            label: d.label,
                          ))
                      .toList(),
                ),
        );
      },
    );
  }

  // İçerik çok geniş ekranlarda uçtan uca yayılmasın: okunabilir bir genişlikte ortalanır.
  Widget _content(Widget child) => Align(
        alignment: Alignment.topCenter,
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 1280),
          child: Padding(
            padding: const EdgeInsets.fromLTRB(24, 24, 24, 20),
            child: child,
          ),
        ),
      );
}

class _Sidebar extends StatelessWidget {
  final List<ShellDestination> destinations;
  final int index;
  final ValueChanged<int> onSelect;
  final VoidCallback onLogout;
  final VoidCallback onChangePassword;

  const _Sidebar({
    required this.destinations,
    required this.index,
    required this.onSelect,
    required this.onLogout,
    required this.onChangePassword,
  });

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context).textTheme;
    return Container(
      width: 240,
      decoration: const BoxDecoration(
        color: AppColors.surface,
        border: Border(right: BorderSide(color: AppColors.border)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // Marka bloğu
          Padding(
            padding: const EdgeInsets.fromLTRB(18, 22, 18, 24),
            child: Row(
              children: [
                const BrandMark(size: 36),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text('VeriYönetim', style: t.titleMedium),
                      Text('Veri platformu', style: t.labelSmall),
                    ],
                  ),
                ),
              ],
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(24, 0, 24, 8),
            child: Text('ÇALIŞMA ALANI', style: t.labelSmall),
          ),
          // Bölümler
          for (var i = 0; i < destinations.length; i++)
            _NavItem(
              destination: destinations[i],
              selected: i == index,
              onTap: () => onSelect(i),
            ),
          const Spacer(),
          // Alt blok: kim olarak girildiği + çıkış
          Padding(
            padding: const EdgeInsets.all(12),
            child: _UserCard(
                onLogout: onLogout, onChangePassword: onChangePassword),
          ),
        ],
      ),
    );
  }
}

class _NavItem extends StatelessWidget {
  final ShellDestination destination;
  final bool selected;
  final VoidCallback onTap;

  const _NavItem({
    required this.destination,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    // Seçili bölüm: markanın soluk bir dolgusu + marka renginde metin/simge.
    final color = selected ? AppColors.brand : AppColors.muted;
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 2),
      child: Material(
        color: selected ? AppColors.brand.withValues(alpha: 0.14) : Colors.transparent,
        borderRadius: BorderRadius.circular(AppRadius.small),
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(AppRadius.small),
          hoverColor: AppColors.brand.withValues(alpha: 0.07),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 11),
            child: Row(
              children: [
                Icon(selected ? destination.activeIcon : destination.icon,
                    size: 19, color: color),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    destination.label,
                    style: TextStyle(
                      color: selected ? AppColors.text : AppColors.muted,
                      fontSize: 13.5,
                      fontWeight: selected ? FontWeight.w600 : FontWeight.w500,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _UserCard extends StatelessWidget {
  final VoidCallback onLogout;
  final VoidCallback onChangePassword;
  const _UserCard({required this.onLogout, required this.onChangePassword});

  @override
  Widget build(BuildContext context) {
    final email = ApiService.currentEmail ?? '—';
    final role = ApiService.currentRole;
    return Container(
      padding: const EdgeInsets.all(10),
      decoration: BoxDecoration(
        color: AppColors.bg,
        border: Border.all(color: AppColors.border),
        borderRadius: BorderRadius.circular(AppRadius.control),
      ),
      child: Row(
        children: [
          _Avatar(email: email),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(email,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600)),
                if (role != null)
                  Text(roleLabels[role] ?? role,
                      style: TextStyle(fontSize: 11, color: RoleBadge.colorOf(role))),
              ],
            ),
          ),
          IconButton(
            onPressed: onChangePassword,
            icon: const Icon(Icons.key_outlined, size: 18),
            tooltip: 'Şifre değiştir',
            visualDensity: VisualDensity.compact,
          ),
          IconButton(
            onPressed: onLogout,
            icon: const Icon(Icons.logout, size: 18),
            tooltip: 'Çıkış yap',
            visualDensity: VisualDensity.compact,
          ),
        ],
      ),
    );
  }
}

class _Avatar extends StatelessWidget {
  final String email;
  const _Avatar({required this.email});

  @override
  Widget build(BuildContext context) {
    final initial = email.isEmpty ? '?' : email.characters.first.toUpperCase();
    return Container(
      width: 30,
      height: 30,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        color: AppColors.brand.withValues(alpha: 0.18),
        shape: BoxShape.circle,
      ),
      child: Text(initial,
          style: const TextStyle(
              color: AppColors.brand, fontSize: 13, fontWeight: FontWeight.w700)),
    );
  }
}

class _TopBar extends StatelessWidget {
  final List<String> breadcrumb;
  final bool showLogout;
  final VoidCallback onLogout;
  final VoidCallback onChangePassword;

  const _TopBar({
    required this.breadcrumb,
    required this.showLogout,
    required this.onLogout,
    required this.onChangePassword,
  });

  @override
  Widget build(BuildContext context) {
    final role = ApiService.currentRole;
    return Container(
      height: 58,
      padding: const EdgeInsets.symmetric(horizontal: 20),
      decoration: const BoxDecoration(
        color: AppColors.surface,
        border: Border(bottom: BorderSide(color: AppColors.border)),
      ),
      child: Row(
        children: [
          if (showLogout) ...[
            const BrandMark(size: 28),
            const SizedBox(width: 10),
          ],
          // Konum izi: son parça vurgulu, öncekiler sönük.
          Expanded(child: _Breadcrumb(parts: breadcrumb)),
          if (role != null) RoleBadge(role: role, compact: true),
          // Dar ekranda sol menü yok → şifre ve çıkış üst çubuğa taşınır.
          if (showLogout) ...[
            IconButton(
              onPressed: onChangePassword,
              icon: const Icon(Icons.key_outlined, size: 19),
              tooltip: 'Şifre değiştir',
            ),
            IconButton(
              onPressed: onLogout,
              icon: const Icon(Icons.logout, size: 19),
              tooltip: 'Çıkış yap',
            ),
          ],
        ],
      ),
    );
  }
}

class _Breadcrumb extends StatelessWidget {
  final List<String> parts;
  const _Breadcrumb({required this.parts});

  @override
  Widget build(BuildContext context) {
    if (parts.isEmpty) return const SizedBox.shrink();
    final spans = <InlineSpan>[];
    for (var i = 0; i < parts.length; i++) {
      final isLast = i == parts.length - 1;
      spans.add(TextSpan(
        text: parts[i],
        style: TextStyle(
          color: isLast ? AppColors.text : AppColors.muted,
          fontSize: 13,
          fontWeight: isLast ? FontWeight.w600 : FontWeight.w500,
        ),
      ));
      if (!isLast) {
        spans.add(const TextSpan(
          text: '  /  ',
          style: TextStyle(color: AppColors.muted, fontSize: 13),
        ));
      }
    }
    return Text.rich(TextSpan(children: spans),
        maxLines: 1, overflow: TextOverflow.ellipsis);
  }
}
