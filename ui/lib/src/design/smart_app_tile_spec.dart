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
  }) {
    final spec = IcAppIcon.resolveSpec(node.appId);
    final resolvedIcon =
        (node.icon != null && node.icon!.isNotEmpty
            ? uiIcon(node.icon!)
            : null) ??
        spec.icon;
    final cleanTitle = node.title.trim().isNotEmpty
        ? node.title.trim()
        : IcAppIcon.humanizeAppTitle(node.appId);
    final cleanCategory =
        (node.category != null && node.category!.trim().isNotEmpty)
        ? node.category!.trim()
        : spec.category;
    final cleanBrain = brain.trim().isNotEmpty ? brain.trim() : 'local';
    final uri = node.appKind == 'unit'
        ? 'neuron://$cleanBrain/units/${node.appId}'
        : 'neuron://$cleanBrain/apps/${node.appId}';
    return SmartAppTileSpec(
      id: node.appId,
      name: cleanTitle,
      icon: resolvedIcon,
      color: spec.gradientTop,
      accentHex: spec.accentHex,
      category: cleanCategory,
      neuronUri: uri,
      launch: node.launch,
      dock: node.dock,
      appKind: node.appKind,
      description: node.description,
      iconSlug: node.icon,
    );
  }

  factory SmartAppTileSpec.fromProjectionTile(
    DesktopAppTile tile, {
    String brain = 'local',
  }) {
    final spec = IcAppIcon.resolveSpec(tile.id);
    final resolvedIcon =
        (tile.icon != null && tile.icon!.isNotEmpty
            ? uiIcon(tile.icon!)
            : null) ??
        spec.icon;
    final cleanTitle = tile.title.trim().isNotEmpty
        ? tile.title.trim()
        : IcAppIcon.humanizeAppTitle(tile.id);
    final cleanCategory =
        (tile.category != null && tile.category!.trim().isNotEmpty)
        ? tile.category!.trim()
        : spec.category;
    final cleanBrain = brain.trim().isNotEmpty ? brain.trim() : 'local';
    final uri = tile.kind == 'unit'
        ? 'neuron://$cleanBrain/units/${tile.id}'
        : 'neuron://$cleanBrain/apps/${tile.id}';
    return SmartAppTileSpec(
      id: tile.id,
      name: cleanTitle,
      icon: resolvedIcon,
      color: spec.gradientTop,
      accentHex: spec.accentHex,
      category: cleanCategory,
      neuronUri: uri,
      launch: tile.launch,
      dock: tile.dock,
      appKind: tile.kind,
      description: tile.description,
      iconSlug: tile.icon,
    );
  }

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
  }) {
    final spec = IcAppIcon.resolveSpec(id);
    final resolvedIcon =
        (iconSlug != null && iconSlug.isNotEmpty ? uiIcon(iconSlug) : null) ??
        spec.icon;
    final cleanTitle =
        (titleOverride != null && titleOverride.trim().isNotEmpty)
        ? titleOverride.trim()
        : spec.title;
    final cleanCategory =
        (categoryOverride != null && categoryOverride.trim().isNotEmpty)
        ? categoryOverride.trim()
        : spec.category;
    final cleanBrain = brain.trim().isNotEmpty ? brain.trim() : 'local';
    return SmartAppTileSpec(
      id: id,
      name: cleanTitle,
      icon: resolvedIcon,
      color: spec.gradientTop,
      accentHex: spec.accentHex,
      category: cleanCategory,
      neuronUri: appKind == 'unit'
          ? 'neuron://$cleanBrain/units/$id'
          : 'neuron://$cleanBrain/apps/$id',
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
