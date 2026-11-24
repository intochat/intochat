import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

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

/// Categorized "All Apps" Launcher modal displaying all apps provided by the
/// desktop view (`AppTileNode` catalog) with search and dynamic category filters.
class AllAppsLauncherOverlay extends StatefulWidget {
  const AllAppsLauncherOverlay({
    super.key,
    this.apps = const [],
    required this.onLaunchApp,
    required this.onClose,
  });

  final List<SmartAppTileSpec> apps;
  final ValueChanged<String> onLaunchApp;
  final VoidCallback onClose;

  @override
  State<AllAppsLauncherOverlay> createState() => _AllAppsLauncherOverlayState();
}

class _AllAppsLauncherOverlayState extends State<AllAppsLauncherOverlay> {
  final TextEditingController _searchController = TextEditingController();
  String _selectedCategory = 'All';

  List<String> _availableCategories() {
    final categories = <String>['All'];
    for (final spec in widget.apps) {
      final cat = spec.category.trim();
      if (cat.isNotEmpty && !categories.contains(cat)) {
        categories.add(cat);
      }
    }
    return categories;
  }

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  List<SmartAppTileSpec> _filteredApps() {
    final q = _searchController.text.trim().toLowerCase();
    return widget.apps.where((spec) {
      if (_selectedCategory != 'All' && spec.category != _selectedCategory) {
        return false;
      }
      if (q.isEmpty) return true;
      return spec.name.toLowerCase().contains(q) ||
          spec.id.toLowerCase().contains(q) ||
          spec.category.toLowerCase().contains(q) ||
          spec.neuronUri.toLowerCase().contains(q);
    }).toList();
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final filtered = _filteredApps();

    return GestureDetector(
      key: const ValueKey('all-apps-launcher-overlay'),
      behavior: HitTestBehavior.opaque,
      onTap: widget.onClose,
      child: Container(
        color: const Color(0x66000000),
        alignment: Alignment.center,
        padding: const EdgeInsets.fromLTRB(32, 28, 32, 64),
        child: GestureDetector(
          onTap: () {},
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 980, maxHeight: 720),
            child: IcAcrylicBox(
              radius: IcRadius.panel,
              padding: const EdgeInsets.all(22),
              shadows: IcShadow.xl(c),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // Header row
                  Row(
                    children: [
                      Icon(LucideIcons.layoutGrid, size: 18, color: c.accent),
                      const SizedBox(width: 10),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              'All Applications & Neurons (${widget.apps.length})',
                              style: IcType.text(
                                16,
                                weight: FontWeight.w700,
                                color: c.textStrong,
                              ),
                            ),
                            Text(
                              'DigitalBrain OS Native Grains · .NET Aspire · ArtCraft Studio (WASM / MCP / CLI)',
                              style: IcType.text(12, color: c.textMuted),
                            ),
                          ],
                        ),
                      ),
                      SizedBox(
                        width: 260,
                        height: 36,
                        child: TextField(
                          key: const ValueKey('all-apps-search-input'),
                          controller: _searchController,
                          onChanged: (_) => setState(() {}),
                          style: IcType.text(13, color: c.textStrong),
                          decoration: InputDecoration(
                            hintText: 'Filter ${widget.apps.length} apps…',
                            hintStyle: IcType.text(12.5, color: c.textMuted),
                            prefixIcon: Icon(
                              LucideIcons.search,
                              size: 14,
                              color: c.textMuted,
                            ),
                            filled: true,
                            fillColor: c.surfaceSunk,
                            contentPadding: const EdgeInsets.symmetric(
                              horizontal: 10,
                              vertical: 8,
                            ),
                            border: OutlineInputBorder(
                              borderRadius: BorderRadius.circular(
                                IcRadius.button,
                              ),
                              borderSide: BorderSide(color: c.borderInput),
                            ),
                            enabledBorder: OutlineInputBorder(
                              borderRadius: BorderRadius.circular(
                                IcRadius.button,
                              ),
                              borderSide: BorderSide(color: c.borderInput),
                            ),
                          ),
                        ),
                      ),
                      const SizedBox(width: 10),
                      Btn(
                        key: const ValueKey('all-apps-close-btn'),
                        label: 'Close',
                        small: true,
                        icon: LucideIcons.x,
                        onTap: widget.onClose,
                      ),
                    ],
                  ),
                  const SizedBox(height: 14),
                  // Category filter bar
                  SingleChildScrollView(
                    scrollDirection: Axis.horizontal,
                    child: Row(
                      children: [
                        for (final cat in _availableCategories())
                          Padding(
                            padding: const EdgeInsets.only(right: 6),
                            child: Btn(
                              key: ValueKey('all-apps-cat-$cat'),
                              label: cat,
                              small: true,
                              primary: _selectedCategory == cat,
                              onTap: () =>
                                  setState(() => _selectedCategory = cat),
                            ),
                          ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 16),
                  // Grid of apps
                  Expanded(
                    child: SingleChildScrollView(
                      child: Wrap(
                        spacing: 12,
                        runSpacing: 12,
                        children: [
                          for (final spec in filtered)
                            _AllAppsLauncherCard(
                              key: ValueKey('all-apps-tile-${spec.id}'),
                              spec: spec,
                              onTap: () {
                                widget.onClose();
                                widget.onLaunchApp(spec.id);
                              },
                            ),
                        ],
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _AllAppsLauncherCard extends StatefulWidget {
  const _AllAppsLauncherCard({
    super.key,
    required this.spec,
    required this.onTap,
  });

  final SmartAppTileSpec spec;
  final VoidCallback onTap;

  @override
  State<_AllAppsLauncherCard> createState() => _AllAppsLauncherCardState();
}

class _AllAppsLauncherCardState extends State<_AllAppsLauncherCard> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final isArtCraft = IcAppIcon.isArtCraftApp(widget.spec.id);
    final runtimeBadge = isArtCraft ? 'WASM · MCP · CLI' : 'Orleans Grain';

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: IcMotion.fast,
          width: 174,
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: _hover ? c.accentTint : c.surfaceSunk,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: _hover ? c.accent : c.borderHairline),
          ),
          child: Row(
            children: [
              IcAppIcon(
                appId: widget.spec.id,
                icon: widget.spec.icon,
                accentHex: widget.spec.accentHex,
                size: 36,
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      widget.spec.name,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(
                        13,
                        weight: FontWeight.w700,
                        color: c.textStrong,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      widget.spec.category,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(11, color: c.textMuted),
                    ),
                    const SizedBox(height: 3),
                    Text(
                      runtimeBadge,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(
                        9.5,
                        weight: FontWeight.w600,
                        color: isArtCraft ? c.accent : c.textMuted,
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
