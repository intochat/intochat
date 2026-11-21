import 'package:flutter/material.dart';
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
  });

  factory SmartAppTileSpec.fromIconSpec(
    String id, {
    String? titleOverride,
    String? categoryOverride,
  }) {
    final spec = IcAppIcon.resolveSpec(id);
    final cleanTitle =
        (titleOverride != null && titleOverride.trim().isNotEmpty)
        ? titleOverride.trim()
        : spec.title;
    return SmartAppTileSpec(
      id: id,
      name: cleanTitle,
      icon: spec.icon,
      color: spec.gradientTop,
      accentHex: spec.accentHex,
      category: categoryOverride ?? spec.category,
      neuronUri: 'neuron://local/apps/$id',
    );
  }

  final String id;
  final String name;
  final IconData icon;
  final Color color;
  final String neuronUri;
  final String? accentHex;
  final String category;
}

const _defaultSmartAppIds = <String>[
  'ino',
  'files',
  'photocraft',
  'filmcraft',
  'creator',
  'engineer',
  'gallery',
  'friends',
  'workspace',
  'settings',
];

const _artCraftAppIds = <String>[
  'artcraft',
  'photocraft',
  'filmcraft',
  'vectorcraft',
  'lightcraft',
  'effectcraft',
  'designcraft',
  'pdfcraft',
  'wordcraft',
  'soundcraft',
  'deckcraft',
  'cadcraft',
  'gridcraft',
  'figcraft',
  'texturecraft',
  'materialcraft',
  'solidcraft',
];

const _coreSmartAppIds = <String>[
  'ino',
  'workspace',
  'files',
  'tasks',
  'calculator',
  'data',
  'creator',
  'engineer',
  'gallery',
  'markdraw',
  'aspire',
  'orleans',
  'devui',
  'widgetbook',
  'trace',
  'friends',
];

final List<SmartAppTileSpec> defaultSmartAppSpecs = List.unmodifiable([
  for (final id in _defaultSmartAppIds) SmartAppTileSpec.fromIconSpec(id),
]);

final List<SmartAppTileSpec> artCraftSmartAppSpecs = List.unmodifiable([
  for (final id in _artCraftAppIds)
    SmartAppTileSpec.fromIconSpec(id, categoryOverride: 'ArtCraft Studio'),
]);

/// Complete catalog of all DigitalBrain OS & ArtCraft Studio apps for the
/// All Apps Launcher and Command Palette (`⌘K`), derived directly from `IcAppIcon.builtInSpecs`.
final List<SmartAppTileSpec> allSmartAppSpecs = List.unmodifiable([
  for (final id in _coreSmartAppIds)
    switch (id) {
      'orleans' => SmartAppTileSpec.fromIconSpec(
        id,
        titleOverride: 'Orleans Dashboard',
        categoryOverride: 'Developer',
      ),
      'devui' => SmartAppTileSpec.fromIconSpec(
        id,
        titleOverride: 'DevUI',
        categoryOverride: 'Developer',
      ),
      'widgetbook' => SmartAppTileSpec.fromIconSpec(
        id,
        titleOverride: 'Widgetbook',
        categoryOverride: 'Developer',
      ),
      _ => SmartAppTileSpec.fromIconSpec(id),
    },
  ...artCraftSmartAppSpecs,
  SmartAppTileSpec.fromIconSpec('settings'),
]);

/// Full-featured categorized "All Apps" Launcher modal displaying all apps
/// (including all 17 `getartcraft.com` apps, `.NET Aspire`, `Ino`, etc.)
/// with search, category filters, execution runtime badges, and custom brand icons.
class AllAppsLauncherOverlay extends StatefulWidget {
  AllAppsLauncherOverlay({
    super.key,
    List<SmartAppTileSpec>? apps,
    required this.onLaunchApp,
    required this.onClose,
  }) : apps = apps ?? allSmartAppSpecs;

  final List<SmartAppTileSpec> apps;
  final ValueChanged<String> onLaunchApp;
  final VoidCallback onClose;

  @override
  State<AllAppsLauncherOverlay> createState() => _AllAppsLauncherOverlayState();
}

class _AllAppsLauncherOverlayState extends State<AllAppsLauncherOverlay> {
  final TextEditingController _searchController = TextEditingController();
  String _selectedCategory = 'All';

  static const List<String> _categories = [
    'All',
    'ArtCraft Studio',
    'Micro-Apps',
    'Intelligence',
    'Workspace',
    'Studio',
    'Developer',
    'Analytics',
    'Social',
    'System',
  ];

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
                        for (final cat in _categories)
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
