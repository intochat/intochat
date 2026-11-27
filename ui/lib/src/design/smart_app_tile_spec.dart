import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../theme/ui_icon.dart' show uiIcon;
import 'app_tile.dart';

class SmartAppTileSpec {
  const SmartAppTileSpec({
    required this.id,
    required this.name,
    required this.icon,
    required this.color,
    required this.neuronUri,
    this.accentHex,
    this.category = 'App',
    this.launch = AppLaunch.windowed,
    this.dock,
    this.appKind = 'compiled',
    this.description,
    this.iconSlug,
  });

  factory SmartAppTileSpec.fromNode(
    AppTileNode node, {
    String brain = 'local',
  }) => _build(
    id: node.appId,
    title: node.title,
    category: node.category,
    launch: node.launch,
    dock: node.dock,
    appKind: node.appKind,
    description: node.description,
    iconSlug: node.icon,
    brain: brain,
  );

  factory SmartAppTileSpec.fromProjectionTile(
    DesktopAppTile tile, {
    String brain = 'local',
  }) => _build(
    id: tile.id,
    title: tile.title,
    category: tile.category,
    launch: tile.launch,
    dock: tile.dock,
    appKind: tile.kind,
    description: tile.description,
    iconSlug: tile.icon,
    brain: brain,
  );

  factory SmartAppTileSpec.fromIconSpec(
    String id, {
    String? titleOverride,
    String? categoryOverride,
    AppLaunch launch = AppLaunch.windowed,
    int? dock,
    String appKind = 'compiled',
    String? description,
    String? iconSlug,
    String brain = 'local',
  }) => _build(
    id: id,
    title: titleOverride,
    category: categoryOverride,
    launch: launch,
    dock: dock,
    appKind: appKind,
    description: description,
    iconSlug: iconSlug,
    brain: brain,
  );

  static SmartAppTileSpec _build({
    required String id,
    String? title,
    String? category,
    required AppLaunch launch,
    int? dock,
    required String appKind,
    String? description,
    String? iconSlug,
    required String brain,
  }) {
    final spec = IcAppIcon.resolveSpec(id);
    final resolvedIcon =
        (iconSlug != null && iconSlug.isNotEmpty ? uiIcon(iconSlug) : null) ??
        spec.icon;
    final trimmedTitle = title?.trim() ?? '';
    final cleanTitle = trimmedTitle.isNotEmpty ? trimmedTitle : spec.title;
    final trimmedCategory = category?.trim() ?? '';
    final cleanCategory = trimmedCategory.isNotEmpty
        ? trimmedCategory
        : spec.category;
    final cleanBrain = brain.trim().isNotEmpty ? brain.trim() : 'local';
    final uri = appKind == 'unit'
        ? 'neuron://$cleanBrain/units/$id'
        : 'neuron://$cleanBrain/apps/$id';
    return SmartAppTileSpec(
      id: id,
      name: cleanTitle,
      icon: resolvedIcon,
      color: spec.gradientTop,
      accentHex: spec.accentHex,
      category: cleanCategory,
      neuronUri: uri,
      launch: launch,
      dock: dock,
      appKind: appKind,
      description: description,
      iconSlug: iconSlug,
    );
  }

  final String id;
  final String name;
  final IconData icon;
  final Color color;
  final String neuronUri;
  final String? accentHex;
  final String category;
  final AppLaunch launch;
  final int? dock;
  final String appKind;
  final String? description;
  final String? iconSlug;
}
