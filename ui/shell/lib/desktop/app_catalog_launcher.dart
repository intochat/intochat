import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

class WhiteboardCardData {
  const WhiteboardCardData({
    required this.id,
    required this.title,
    this.icon,
    this.content,
    this.initialBounds,
    this.isPinned = false,
  });

  final String id;
  final String title;
  final IconData? icon;
  final Map<String, dynamic>? content;
  final Rect? initialBounds;
  final bool isPinned;
}

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

  final String id;
  final String name;
  final IconData icon;
  final Color color;
  final String neuronUri;
  final String? accentHex;
  final String category;
}

const defaultSmartAppSpecs = <SmartAppTileSpec>[
  SmartAppTileSpec(
    id: 'ino',
    name: 'Ino',
    icon: LucideIcons.sparkles,
    color: Color(0xFF3D7EFF),
    accentHex: '#3D7EFF',
    category: 'Intelligence',
    neuronUri: 'neuron://local/apps/ino',
  ),
  SmartAppTileSpec(
    id: 'files',
    name: 'Files',
    icon: LucideIcons.folder,
    color: Color(0xFFF6B73C),
    accentHex: '#F6B73C',
    category: 'Workspace',
    neuronUri: 'neuron://local/apps/files',
  ),
  SmartAppTileSpec(
    id: 'photocraft',
    name: 'PhotoCraft',
    icon: LucideIcons.image,
    color: Color(0xFF06B6D4),
    accentHex: '#06B6D4',
    category: 'Studio',
    neuronUri: 'neuron://local/apps/photocraft',
  ),
  SmartAppTileSpec(
    id: 'filmcraft',
    name: 'FilmCraft',
    icon: LucideIcons.film,
    color: Color(0xFF8B5CF6),
    accentHex: '#8B5CF6',
    category: 'Studio',
    neuronUri: 'neuron://local/apps/filmcraft',
  ),
  SmartAppTileSpec(
    id: 'creator',
    name: 'Creator',
    icon: LucideIcons.wandSparkles,
    color: Color(0xFFFF5D8F),
    accentHex: '#FF5D8F',
    category: 'Studio',
    neuronUri: 'neuron://local/apps/creator',
  ),
  SmartAppTileSpec(
    id: 'gallery',
    name: 'UI Gallery',
    icon: LucideIcons.palette,
    color: Color(0xFF22C55E),
    accentHex: '#22C55E',
    category: 'Studio',
    neuronUri: 'neuron://local/apps/gallery',
  ),
  SmartAppTileSpec(
    id: 'post',
    name: 'Post',
    icon: LucideIcons.megaphone,
    color: Color(0xFFFB7185),
    accentHex: '#FB7185',
    category: 'Reach',
    neuronUri: 'neuron://local/apps/post',
  ),
  SmartAppTileSpec(
    id: 'circles',
    name: 'Circles',
    icon: LucideIcons.circleDot,
    color: Color(0xFFA855F7),
    accentHex: '#A855F7',
    category: 'Social',
    neuronUri: 'neuron://local/apps/circles',
  ),
  SmartAppTileSpec(
    id: 'workspace',
    name: 'Workspace',
    icon: LucideIcons.compass,
    color: Color(0xFF14B8A6),
    accentHex: '#14B8A6',
    category: 'Workspace',
    neuronUri: 'neuron://local/apps/workspace',
  ),
  SmartAppTileSpec(
    id: 'brain',
    name: 'Brain',
    icon: LucideIcons.brainCircuit,
    color: Color(0xFF6366F1),
    accentHex: '#6366F1',
    category: 'Studio',
    neuronUri: 'neuron://local/apps/brain',
  ),
  SmartAppTileSpec(
    id: 'settings',
    name: 'Settings',
    icon: LucideIcons.slidersHorizontal,
    color: Color(0xFF64748B),
    accentHex: '#64748B',
    category: 'System',
    neuronUri: 'neuron://local/apps/settings',
  ),
];

const artCraftSmartAppSpecs = <SmartAppTileSpec>[
  SmartAppTileSpec(
    id: 'artcraft',
    name: 'ArtCraft',
    icon: LucideIcons.brush,
    color: Color(0xFFF43F5E),
    accentHex: '#F43F5E',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/artcraft',
  ),
  SmartAppTileSpec(
    id: 'photocraft',
    name: 'PhotoCraft',
    icon: LucideIcons.image,
    color: Color(0xFF06B6D4),
    accentHex: '#06B6D4',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/photocraft',
  ),
  SmartAppTileSpec(
    id: 'filmcraft',
    name: 'FilmCraft',
    icon: LucideIcons.film,
    color: Color(0xFF8B5CF6),
    accentHex: '#8B5CF6',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/filmcraft',
  ),
  SmartAppTileSpec(
    id: 'vectorcraft',
    name: 'VectorCraft',
    icon: LucideIcons.penTool,
    color: Color(0xFFF97316),
    accentHex: '#F97316',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/vectorcraft',
  ),
  SmartAppTileSpec(
    id: 'lightcraft',
    name: 'LightCraft',
    icon: LucideIcons.sunMedium,
    color: Color(0xFFEAB308),
    accentHex: '#EAB308',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/lightcraft',
  ),
  SmartAppTileSpec(
    id: 'effectcraft',
    name: 'EffectCraft',
    icon: LucideIcons.wand,
    color: Color(0xFFA855F7),
    accentHex: '#A855F7',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/effectcraft',
  ),
  SmartAppTileSpec(
    id: 'designcraft',
    name: 'DesignCraft',
    icon: LucideIcons.layers,
    color: Color(0xFFEC4899),
    accentHex: '#EC4899',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/designcraft',
  ),
  SmartAppTileSpec(
    id: 'pdfcraft',
    name: 'PDFCraft',
    icon: LucideIcons.fileText,
    color: Color(0xFFEF4444),
    accentHex: '#EF4444',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/pdfcraft',
  ),
  SmartAppTileSpec(
    id: 'wordcraft',
    name: 'WordCraft',
    icon: LucideIcons.fileEdit,
    color: Color(0xFF2563EB),
    accentHex: '#2563EB',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/wordcraft',
  ),
  SmartAppTileSpec(
    id: 'soundcraft',
    name: 'SoundCraft',
    icon: LucideIcons.music,
    color: Color(0xFF10B981),
    accentHex: '#10B981',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/soundcraft',
  ),
  SmartAppTileSpec(
    id: 'deckcraft',
    name: 'DeckCraft',
    icon: LucideIcons.presentation,
    color: Color(0xFFF59E0B),
    accentHex: '#F59E0B',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/deckcraft',
  ),
  SmartAppTileSpec(
    id: 'cadcraft',
    name: 'CADCraft',
    icon: LucideIcons.compass,
    color: Color(0xFF0EA5E9),
    accentHex: '#0EA5E9',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/cadcraft',
  ),
  SmartAppTileSpec(
    id: 'gridcraft',
    name: 'GridCraft',
    icon: LucideIcons.table,
    color: Color(0xFF16A34A),
    accentHex: '#16A34A',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/gridcraft',
  ),
  SmartAppTileSpec(
    id: 'figcraft',
    name: 'FigCraft',
    icon: LucideIcons.penTool,
    color: Color(0xFFD946EF),
    accentHex: '#D946EF',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/figcraft',
  ),
  SmartAppTileSpec(
    id: 'texturecraft',
    name: 'TextureCraft',
    icon: LucideIcons.grid,
    color: Color(0xFF14B8A6),
    accentHex: '#14B8A6',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/texturecraft',
  ),
  SmartAppTileSpec(
    id: 'materialcraft',
    name: 'MaterialCraft',
    icon: LucideIcons.droplet,
    color: Color(0xFF6366F1),
    accentHex: '#6366F1',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/materialcraft',
  ),
  SmartAppTileSpec(
    id: 'solidcraft',
    name: 'SolidCraft',
    icon: LucideIcons.box,
    color: Color(0xFF3B82F6),
    accentHex: '#3B82F6',
    category: 'ArtCraft Studio',
    neuronUri: 'neuron://local/apps/solidcraft',
  ),
];

/// Complete catalog of all 31 DigitalBrain OS & ArtCraft Studio apps for the
/// All Apps Launcher and Command Palette (`⌘K`).
const allSmartAppSpecs = <SmartAppTileSpec>[
  SmartAppTileSpec(
    id: 'ino',
    name: 'Ino',
    icon: LucideIcons.sparkles,
    color: Color(0xFF3D7EFF),
    accentHex: '#3D7EFF',
    category: 'Intelligence',
    neuronUri: 'neuron://local/apps/ino',
  ),
  SmartAppTileSpec(
    id: 'brain',
    name: 'Brain',
    icon: LucideIcons.brainCircuit,
    color: Color(0xFF6366F1),
    accentHex: '#6366F1',
    category: 'Intelligence',
    neuronUri: 'neuron://local/apps/brain',
  ),
  SmartAppTileSpec(
    id: 'workspace',
    name: 'Workspace',
    icon: LucideIcons.compass,
    color: Color(0xFF14B8A6),
    accentHex: '#14B8A6',
    category: 'Workspace',
    neuronUri: 'neuron://local/apps/workspace',
  ),
  SmartAppTileSpec(
    id: 'files',
    name: 'Files',
    icon: LucideIcons.folder,
    color: Color(0xFFF6B73C),
    accentHex: '#F6B73C',
    category: 'Workspace',
    neuronUri: 'neuron://local/apps/files',
  ),
  SmartAppTileSpec(
    id: 'tasks',
    name: 'Tasks',
    icon: LucideIcons.listTodo,
    color: Color(0xFF10B981),
    accentHex: '#10B981',
    category: 'Workspace',
    neuronUri: 'neuron://local/apps/tasks',
  ),
  SmartAppTileSpec(
    id: 'calculator',
    name: 'Calculator',
    icon: LucideIcons.calculator,
    color: Color(0xFFF59E0B),
    accentHex: '#F59E0B',
    category: 'Workspace',
    neuronUri: 'neuron://local/apps/calculator',
  ),
  SmartAppTileSpec(
    id: 'data',
    name: 'Data Studio',
    icon: LucideIcons.barChart2,
    color: Color(0xFF10B981),
    accentHex: '#10B981',
    category: 'Analytics',
    neuronUri: 'neuron://local/apps/data',
  ),
  SmartAppTileSpec(
    id: 'creator',
    name: 'Creator',
    icon: LucideIcons.wandSparkles,
    color: Color(0xFFFF5D8F),
    accentHex: '#FF5D8F',
    category: 'Studio',
    neuronUri: 'neuron://local/apps/creator',
  ),
  SmartAppTileSpec(
    id: 'gallery',
    name: 'UI Gallery',
    icon: LucideIcons.palette,
    color: Color(0xFF22C55E),
    accentHex: '#22C55E',
    category: 'Studio',
    neuronUri: 'neuron://local/apps/gallery',
  ),
  SmartAppTileSpec(
    id: 'markdraw',
    name: 'Markdraw',
    icon: LucideIcons.penTool,
    color: Color(0xFFF59E0B),
    accentHex: '#F59E0B',
    category: 'Studio',
    neuronUri: 'neuron://local/apps/markdraw',
  ),
  SmartAppTileSpec(
    id: 'aspire',
    name: '.NET Aspire',
    icon: LucideIcons.cpu,
    color: Color(0xFF512BD4),
    accentHex: '#512BD4',
    category: 'Developer',
    neuronUri: 'neuron://local/apps/aspire',
  ),
  SmartAppTileSpec(
    id: 'trace',
    name: 'Trace',
    icon: LucideIcons.activity,
    color: Color(0xFF06B6D4),
    accentHex: '#06B6D4',
    category: 'Developer',
    neuronUri: 'neuron://local/apps/trace',
  ),
  SmartAppTileSpec(
    id: 'post',
    name: 'Post',
    icon: LucideIcons.megaphone,
    color: Color(0xFFFB7185),
    accentHex: '#FB7185',
    category: 'Social',
    neuronUri: 'neuron://local/apps/post',
  ),
  SmartAppTileSpec(
    id: 'circles',
    name: 'Circles',
    icon: LucideIcons.circleDot,
    color: Color(0xFFA855F7),
    accentHex: '#A855F7',
    category: 'Social',
    neuronUri: 'neuron://local/apps/circles',
  ),
  ...artCraftSmartAppSpecs,
  SmartAppTileSpec(
    id: 'settings',
    name: 'Settings',
    icon: LucideIcons.slidersHorizontal,
    color: Color(0xFF64748B),
    accentHex: '#64748B',
    category: 'System',
    neuronUri: 'neuron://local/apps/settings',
  ),
];

/// Full-featured categorized "All Apps" Launcher modal displaying all 31+ apps
/// (including all 17 `getartcraft.com` apps, `.NET Aspire`, `Brain`, `Ino`, etc.)
/// with search, category filters, execution runtime badges, and custom brand icons.
class AllAppsLauncherOverlay extends StatefulWidget {
  const AllAppsLauncherOverlay({
    super.key,
    this.apps = allSmartAppSpecs,
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

  static const List<String> _categories = [
    'All',
    'ArtCraft Studio',
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
