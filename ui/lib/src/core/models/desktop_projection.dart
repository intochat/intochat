import '../../generated/node_models.dart' show AppLaunch;

/// Dart mirror of `DesktopTabProjection` in `src/IntoChat.OS.Contracts/Shell/DesktopShellProjection.cs`.
final class DesktopTabProjection {
  const DesktopTabProjection({
    required this.id,
    required this.appId,
    required this.title,
    required this.selected,
  });

  final String id;
  final String appId;
  final String title;
  final bool selected;

  factory DesktopTabProjection.fromJson(Map<String, dynamic> json) =>
      DesktopTabProjection(
        id: (json['id'] as String?) ?? '',
        appId: (json['appId'] as String?) ?? '',
        title: (json['title'] as String?) ?? '',
        selected: json['selected'] == true,
      );

  Map<String, dynamic> toJson() => {
    'id': id,
    'appId': appId,
    'title': title,
    'selected': selected,
  };
}

/// Dart mirror of `DesktopWindowProjection` in `src/IntoChat.OS.Contracts/Shell/DesktopShellProjection.cs`.
final class DesktopWindowProjection {
  const DesktopWindowProjection({
    required this.id,
    required this.appId,
    required this.title,
    required this.viewportUri,
    required this.tabs,
    required this.activeTabId,
    this.isPinned = false,
    this.isFavourite = false,
  });

  final String id;
  final String appId;
  final String title;
  final String viewportUri;
  final List<DesktopTabProjection> tabs;
  final String activeTabId;
  final bool isPinned;
  final bool isFavourite;

  int get numericId {
    final digits = RegExp(r'(\d+)$').firstMatch(id)?.group(1);
    return digits != null ? int.parse(digits) : id.hashCode.abs() % 999 + 1;
  }

  int get activeTabIndex {
    final idx = tabs.indexWhere((t) => t.id == activeTabId || t.selected);
    return idx >= 0 ? idx : 0;
  }

  factory DesktopWindowProjection.fromJson(Map<String, dynamic> json) {
    final rawTabs = json['tabs'];
    return DesktopWindowProjection(
      id: (json['id'] as String?) ?? '',
      appId: (json['appId'] as String?) ?? '',
      title: (json['title'] as String?) ?? '',
      viewportUri: (json['viewportUri'] as String?) ?? '',
      tabs: rawTabs is List
          ? [
              for (final item in rawTabs)
                if (item is Map)
                  DesktopTabProjection.fromJson(
                    Map<String, dynamic>.from(item),
                  ),
            ]
          : const [],
      activeTabId: (json['activeTabId'] as String?) ?? '',
      isPinned: json['isPinned'] == true,
      isFavourite: json['isFavourite'] == true,
    );
  }

  Map<String, dynamic> toJson() => {
    'id': id,
    'appId': appId,
    'title': title,
    'viewportUri': viewportUri,
    'tabs': [for (final t in tabs) t.toJson()],
    'activeTabId': activeTabId,
    'isPinned': isPinned,
    'isFavourite': isFavourite,
  };
}

/// Dart mirror of `AppTile` in `src/IntoChat.OS.Contracts/Catalog/Apps/IInstalledApps.cs`.
final class DesktopAppTile {
  const DesktopAppTile({
    required this.id,
    required this.title,
    this.icon,
    this.category,
    this.dock,
    this.launch = AppLaunch.windowed,
    this.kind = 'compiled',
    this.description,
  });

  final String id;
  final String title;
  final String? icon;
  final String? category;
  final int? dock;
  final AppLaunch launch;
  final String kind;
  final String? description;

  static AppLaunch _parseLaunch(Object? raw) {
    if (raw is int) {
      return raw == 1 ? AppLaunch.maximized : AppLaunch.windowed;
    }
    if (raw is String) {
      return raw.trim().toLowerCase() == 'maximized'
          ? AppLaunch.maximized
          : AppLaunch.windowed;
    }
    return AppLaunch.windowed;
  }

  factory DesktopAppTile.fromJson(Map<String, dynamic> json) => DesktopAppTile(
    id: (json['id'] as String?) ?? '',
    title: (json['title'] as String?) ?? '',
    icon: json['icon'] as String?,
    category: json['category'] as String?,
    dock: (json['dock'] as num?)?.toInt(),
    launch: _parseLaunch(json['launch']),
    kind: (json['kind'] as String?) ?? 'compiled',
    description: json['description'] as String?,
  );

  Map<String, dynamic> toJson() => {
    'id': id,
    'title': title,
    'icon': icon,
    'category': category,
    'dock': dock,
    'launch': launch == AppLaunch.maximized ? 1 : 0,
    'kind': kind,
    'description': description,
  };
}

/// Dart mirror of `DesktopShellProjection` in `src/IntoChat.OS.Contracts/Shell/DesktopShellProjection.cs`.
final class DesktopShellProjection {
  const DesktopShellProjection({
    this.windows = const [],
    this.focusedViewId,
    this.pins = const [],
    this.tiles = const [],
    this.openWidgets = const [],
    this.stylePreset = 'warm-paper',
    this.barLayout = 'floating-dock',
    this.layoutMode = 'smart-desktop',
    this.isNarrow = false,
  });

  final List<DesktopWindowProjection> windows;
  final String? focusedViewId;
  final List<String> pins;
  final List<DesktopAppTile> tiles;
  final List<String> openWidgets;
  final String stylePreset;
  final String barLayout;
  final String layoutMode;
  final bool isNarrow;

  factory DesktopShellProjection.fromJson(Map<String, dynamic> json) {
    final rawWindows = json['windows'];
    final rawPins = json['pins'];
    final rawTiles = json['tiles'];
    final rawWidgets = json['openWidgets'];
    return DesktopShellProjection(
      windows: rawWindows is List
          ? [
              for (final item in rawWindows)
                if (item is Map)
                  DesktopWindowProjection.fromJson(
                    Map<String, dynamic>.from(item),
                  ),
            ]
          : const [],
      focusedViewId: json['focusedViewId'] as String?,
      pins: rawPins is List
          ? [
              for (final item in rawPins)
                if (item is String) item,
            ]
          : const [],
      tiles: rawTiles is List
          ? [
              for (final item in rawTiles)
                if (item is Map)
                  DesktopAppTile.fromJson(Map<String, dynamic>.from(item)),
            ]
          : const [],
      openWidgets: rawWidgets is List
          ? [
              for (final item in rawWidgets)
                if (item is String) item,
            ]
          : const [],
      stylePreset: (json['stylePreset'] as String?) ?? 'warm-paper',
      barLayout: (json['barLayout'] as String?) ?? 'floating-dock',
      layoutMode: (json['layoutMode'] as String?) ?? 'smart-desktop',
      isNarrow: json['isNarrow'] == true,
    );
  }

  Map<String, dynamic> toJson() => {
    'windows': [for (final w in windows) w.toJson()],
    'focusedViewId': focusedViewId,
    'pins': pins,
    'tiles': [for (final t in tiles) t.toJson()],
    'openWidgets': openWidgets,
    'stylePreset': stylePreset,
    'barLayout': barLayout,
    'layoutMode': layoutMode,
    'isNarrow': isNarrow,
  };
}
