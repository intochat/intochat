import 'dart:math' as math;

import 'package:flutter/widgets.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import 'tokens.dart';

enum AppTileStyle { glass, brand, outline }

/// Bespoke multi-layered vector and SVG brand glyphs for IntoChat OS apps.
enum IcBrandGlyph {
  inoStar,
  aspireLogo,
  brainGraph,
  workspaceCanvas,
  filesVault,
  dataStudio,
  markdrawCanvas,
  creatorLoop,
  uiGallery,
  tasksBoard,
  traceTelemetry,
  postReach,
  circlesSocial,
  calculatorGrid,
  settingsGear,
  solidcraftCube,
}

/// Metadata for an IntoChat OS app icon badge (squircle gradient + vector/SVG glyph).
@immutable
class IcAppIconSpec {
  const IcAppIconSpec({
    required this.appId,
    required this.title,
    required this.category,
    required this.gradientTop,
    required this.gradientBottom,
    required this.icon,
    this.glyphColor = const Color(0xFFFFFFFF),
    this.useInoStar = false,
    this.brandGlyph,
    this.svgAsset,
    this.pngAsset,
    this.isArtCraftBridge = false,
    this.defaultWebPort,
  });

  final String appId;
  final String title;
  final String category;
  final Color gradientTop;
  final Color gradientBottom;
  final IconData icon;
  final Color glyphColor;
  final bool useInoStar;
  final IcBrandGlyph? brandGlyph;
  final String? svgAsset;
  final String? pngAsset;
  final bool isArtCraftBridge;
  final int? defaultWebPort;

  String get accentHex =>
      '#${gradientTop.toARGB32().toRadixString(16).padLeft(8, '0').substring(2).toUpperCase()}';
}

/// First-class squircle app icon badge (`IcAppIcon`) with a two-stop gradient
/// squircle (corner radius 22% of size), 1px specular border, and custom SVG/vector
/// brand glyph or Lucide fallback.
class IcAppIcon extends StatelessWidget {
  const IcAppIcon({
    super.key,
    required this.appId,
    this.icon,
    this.accentHex,
    this.size = 32,
    this.gradientTop,
    this.gradientBottom,
    this.glyphColor,
  });

  final String appId;
  final IconData? icon;
  final String? accentHex;
  final double size;
  final Color? gradientTop;
  final Color? gradientBottom;
  final Color? glyphColor;

  static const Map<String, IcAppIconSpec> builtInSpecs = {
    'ino': IcAppIconSpec(
      appId: 'ino',
      title: 'Ino',
      category: 'Intelligence',
      gradientTop: Color(0xFF3D7EFF),
      gradientBottom: Color(0xFF1948C8),
      icon: LucideIcons.sparkles,
      useInoStar: true,
      brandGlyph: IcBrandGlyph.inoStar,
    ),
    'aspire': IcAppIconSpec(
      appId: 'aspire',
      title: '.NET Aspire',
      category: 'Developer',
      gradientTop: Color(0xFF2D1674),
      gradientBottom: Color(0xFF150A3A),
      icon: LucideIcons.activity,
      brandGlyph: IcBrandGlyph.aspireLogo,
      pngAsset: 'assets/brands/aspire.png',
      svgAsset: 'assets/brands/aspire-icon-32.svg',
    ),
    'brain': IcAppIconSpec(
      appId: 'brain',
      title: 'Brain',
      category: 'Intelligence',
      gradientTop: Color(0xFF6366F1),
      gradientBottom: Color(0xFF312E81),
      icon: LucideIcons.brainCircuit,
      brandGlyph: IcBrandGlyph.brainGraph,
    ),
    'workspace': IcAppIconSpec(
      appId: 'workspace',
      title: 'Workspace',
      category: 'Workspace',
      gradientTop: Color(0xFF14B8A6),
      gradientBottom: Color(0xFF0F766E),
      icon: LucideIcons.compass,
      brandGlyph: IcBrandGlyph.workspaceCanvas,
    ),
    'files': IcAppIconSpec(
      appId: 'files',
      title: 'Files',
      category: 'Workspace',
      gradientTop: Color(0xFFF6B73C),
      gradientBottom: Color(0xFFC97B10),
      icon: LucideIcons.folder,
      brandGlyph: IcBrandGlyph.filesVault,
    ),
    'creator': IcAppIconSpec(
      appId: 'creator',
      title: 'Creator',
      category: 'Studio',
      gradientTop: Color(0xFFFF5D8F),
      gradientBottom: Color(0xFFB91C68),
      icon: LucideIcons.wandSparkles,
      brandGlyph: IcBrandGlyph.creatorLoop,
    ),
    'gallery': IcAppIconSpec(
      appId: 'gallery',
      title: 'UI Gallery',
      category: 'Studio',
      gradientTop: Color(0xFF22C55E),
      gradientBottom: Color(0xFF0D8A3E),
      icon: LucideIcons.palette,
      brandGlyph: IcBrandGlyph.uiGallery,
    ),
    'tasks': IcAppIconSpec(
      appId: 'tasks',
      title: 'Tasks',
      category: 'Workspace',
      gradientTop: Color(0xFF8B5CF6),
      gradientBottom: Color(0xFF5B21B6),
      icon: LucideIcons.listTodo,
      brandGlyph: IcBrandGlyph.tasksBoard,
    ),
    'trace': IcAppIconSpec(
      appId: 'trace',
      title: 'Trace',
      category: 'Developer',
      gradientTop: Color(0xFF06B6D4),
      gradientBottom: Color(0xFF0E7490),
      icon: LucideIcons.activity,
      brandGlyph: IcBrandGlyph.traceTelemetry,
    ),
    'friends': IcAppIconSpec(
      appId: 'friends',
      title: 'Friends',
      category: 'Social',
      gradientTop: Color(0xFFFB7185),
      gradientBottom: Color(0xFFBE123C),
      icon: LucideIcons.users,
      brandGlyph: IcBrandGlyph.circlesSocial,
    ),
    'post': IcAppIconSpec(
      appId: 'post',
      title: 'Post',
      category: 'Reach',
      gradientTop: Color(0xFFFB7185),
      gradientBottom: Color(0xFFBE123C),
      icon: LucideIcons.megaphone,
      brandGlyph: IcBrandGlyph.postReach,
    ),
    'settings': IcAppIconSpec(
      appId: 'settings',
      title: 'Settings',
      category: 'System',
      gradientTop: Color(0xFF64748B),
      gradientBottom: Color(0xFF334155),
      icon: LucideIcons.slidersHorizontal,
      brandGlyph: IcBrandGlyph.settingsGear,
    ),
    'circles': IcAppIconSpec(
      appId: 'circles',
      title: 'Circles',
      category: 'Social',
      gradientTop: Color(0xFFA855F7),
      gradientBottom: Color(0xFF6D28D9),
      icon: LucideIcons.circleDot,
      brandGlyph: IcBrandGlyph.circlesSocial,
    ),
    'calculator': IcAppIconSpec(
      appId: 'calculator',
      title: 'Calculator',
      category: 'Utilities',
      gradientTop: Color(0xFF14B8A6),
      gradientBottom: Color(0xFF0F766E),
      icon: LucideIcons.calculator,
      brandGlyph: IcBrandGlyph.calculatorGrid,
    ),
    'data': IcAppIconSpec(
      appId: 'data',
      title: 'Data Studio',
      category: 'Analytics',
      gradientTop: Color(0xFF38BDF8),
      gradientBottom: Color(0xFF0284C7),
      icon: LucideIcons.chartSpline,
      brandGlyph: IcBrandGlyph.dataStudio,
    ),
    'datavisualizer': IcAppIconSpec(
      appId: 'datavisualizer',
      title: 'Data Visualizer',
      category: 'Analytics',
      gradientTop: Color(0xFF38BDF8),
      gradientBottom: Color(0xFF0284C7),
      icon: LucideIcons.chartSpline,
      brandGlyph: IcBrandGlyph.dataStudio,
    ),
    'markdraw': IcAppIconSpec(
      appId: 'markdraw',
      title: 'Markdraw',
      category: 'Studio',
      gradientTop: Color(0xFF10B981),
      gradientBottom: Color(0xFF047857),
      icon: LucideIcons.penTool,
      brandGlyph: IcBrandGlyph.markdrawCanvas,
    ),
    'photocraft': IcAppIconSpec(
      appId: 'photocraft',
      title: 'PhotoCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFF06B6D4),
      gradientBottom: Color(0xFF0369A1),
      icon: LucideIcons.image,
      pngAsset: 'assets/brands/artcraft/photocraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18082,
    ),
    'filmcraft': IcAppIconSpec(
      appId: 'filmcraft',
      title: 'FilmCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFFF43F5E),
      gradientBottom: Color(0xFF9F1239),
      icon: LucideIcons.film,
      pngAsset: 'assets/brands/artcraft/filmcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18083,
    ),
    'artcraft': IcAppIconSpec(
      appId: 'artcraft',
      title: 'ArtCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFF6366F1),
      gradientBottom: Color(0xFF4338CA),
      icon: LucideIcons.paintbrush,
      pngAsset: 'assets/brands/artcraft/artcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18080,
    ),
    'vectorcraft': IcAppIconSpec(
      appId: 'vectorcraft',
      title: 'VectorCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFFF59E0B),
      gradientBottom: Color(0xFFB45309),
      icon: LucideIcons.penTool,
      pngAsset: 'assets/brands/artcraft/vectorcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18084,
    ),
    'lightcraft': IcAppIconSpec(
      appId: 'lightcraft',
      title: 'LightCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFFEAB308),
      gradientBottom: Color(0xFFA16207),
      icon: LucideIcons.sun,
      pngAsset: 'assets/brands/artcraft/lightcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18085,
    ),
    'effectcraft': IcAppIconSpec(
      appId: 'effectcraft',
      title: 'EffectCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFFA855F7),
      gradientBottom: Color(0xFF6B21A8),
      icon: LucideIcons.wandSparkles,
      pngAsset: 'assets/brands/artcraft/effectcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18086,
    ),
    'designcraft': IcAppIconSpec(
      appId: 'designcraft',
      title: 'DesignCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFFEC4899),
      gradientBottom: Color(0xFF9D174D),
      icon: LucideIcons.layoutGrid,
      pngAsset: 'assets/brands/artcraft/designcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18087,
    ),
    'pdfcraft': IcAppIconSpec(
      appId: 'pdfcraft',
      title: 'PDFCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFFEF4444),
      gradientBottom: Color(0xFF991B1B),
      icon: LucideIcons.fileText,
      pngAsset: 'assets/brands/artcraft/pdfcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18088,
    ),
    'wordcraft': IcAppIconSpec(
      appId: 'wordcraft',
      title: 'WordCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFF2563EB),
      gradientBottom: Color(0xFF1E40AF),
      icon: LucideIcons.fileText,
      pngAsset: 'assets/brands/artcraft/wordcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18089,
    ),
    'soundcraft': IcAppIconSpec(
      appId: 'soundcraft',
      title: 'SoundCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFF10B981),
      gradientBottom: Color(0xFF065F46),
      icon: LucideIcons.music,
      pngAsset: 'assets/brands/artcraft/soundcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18090,
    ),
    'deckcraft': IcAppIconSpec(
      appId: 'deckcraft',
      title: 'DeckCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFFF97316),
      gradientBottom: Color(0xFF9A3412),
      icon: LucideIcons.presentation,
      pngAsset: 'assets/brands/artcraft/deckcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18091,
    ),
    'cadcraft': IcAppIconSpec(
      appId: 'cadcraft',
      title: 'CADCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFF0EA5E9),
      gradientBottom: Color(0xFF075985),
      icon: LucideIcons.box,
      pngAsset: 'assets/brands/artcraft/cadcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18092,
    ),
    'gridcraft': IcAppIconSpec(
      appId: 'gridcraft',
      title: 'GridCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFF16A34A),
      gradientBottom: Color(0xFF14532D),
      icon: LucideIcons.table,
      pngAsset: 'assets/brands/artcraft/gridcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18093,
    ),
    'figcraft': IcAppIconSpec(
      appId: 'figcraft',
      title: 'FigCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFFD946EF),
      gradientBottom: Color(0xFF86198F),
      icon: LucideIcons.penTool,
      pngAsset: 'assets/brands/artcraft/figcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18094,
    ),
    'texturecraft': IcAppIconSpec(
      appId: 'texturecraft',
      title: 'TextureCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFFD97706),
      gradientBottom: Color(0xFF78350F),
      icon: LucideIcons.paintbrush,
      pngAsset: 'assets/brands/artcraft/texturecraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18095,
    ),
    'materialcraft': IcAppIconSpec(
      appId: 'materialcraft',
      title: 'MaterialCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFFEA580C),
      gradientBottom: Color(0xFF7C2D12),
      icon: LucideIcons.layers,
      pngAsset: 'assets/brands/artcraft/materialcraft.png',
      isArtCraftBridge: true,
      defaultWebPort: 18096,
    ),
    'solidcraft': IcAppIconSpec(
      appId: 'solidcraft',
      title: 'SolidCraft',
      category: 'ArtCraft Studio',
      gradientTop: Color(0xFF14B8A6),
      gradientBottom: Color(0xFF115E59),
      icon: LucideIcons.boxes,
      pngAsset: 'assets/brands/artcraft/solidcraft.png',
      brandGlyph: IcBrandGlyph.solidcraftCube,
      isArtCraftBridge: true,
      defaultWebPort: 18097,
    ),
  };

  static bool isArtCraftApp(String? rawAppId) {
    if (rawAppId == null || rawAppId.trim().isEmpty) return false;
    final norm = normalizeAppId(rawAppId);
    return builtInSpecs[norm]?.isArtCraftBridge == true;
  }

  static IcAppIconSpec? findArtCraftByUrlOrName(String? raw) {
    if (raw == null || raw.trim().isEmpty) return null;
    final lower = raw.toLowerCase();
    if (lower.contains('18110')) return builtInSpecs['photocraft'];
    if (lower.contains('18111') || lower.contains('videocraft')) {
      return builtInSpecs['filmcraft'];
    }
    for (final spec in builtInSpecs.values) {
      if (!spec.isArtCraftBridge) continue;
      if (lower.contains(spec.appId) ||
          (spec.defaultWebPort != null &&
              lower.contains('${spec.defaultWebPort}'))) {
        return spec;
      }
    }
    return null;
  }

  /// Resolves a studio WebBrowser URL to the same-origin Kernel `/artcraft/<id>/index.html`
  /// endpoint when running on Web (`isWeb == true`) and the raw URL points to localhost or
  /// a relative `/artcraft/` path.
  static String resolveArtCraftWebUrl(
    String rawUrl, {
    String? hint,
    required bool isWeb,
    Uri? baseUri,
    String? uiBaseOrigin,
  }) {
    final trimmed = rawUrl.trim();
    if (!isWeb) return trimmed;
    final base = baseUri ?? Uri.base;
    if (base.scheme != 'http' && base.scheme != 'https') return trimmed;

    final craftSpec = findArtCraftByUrlOrName('${hint ?? ''} $trimmed');
    if (craftSpec == null) return trimmed;

    final isLocalOrEmpty =
        trimmed.isEmpty ||
        trimmed.startsWith('/artcraft/') ||
        trimmed.contains('localhost') ||
        trimmed.contains('127.0.0.1');
    if (!isLocalOrEmpty) return trimmed;

    String origin = base.origin;
    if (uiBaseOrigin != null && uiBaseOrigin.isNotEmpty) {
      final parsed = Uri.tryParse(uiBaseOrigin);
      if (parsed != null && parsed.hasAuthority) {
        origin = parsed.origin;
      }
    }
    final parsedRaw = Uri.tryParse(trimmed);
    final docQuery = parsedRaw?.queryParameters['doc'];
    if (docQuery != null && docQuery.isNotEmpty) {
      return '$origin/artcraft/${craftSpec.appId}/index.html?doc=${Uri.encodeQueryComponent(docQuery)}';
    }
    return '$origin/artcraft/${craftSpec.appId}/index.html';
  }

  static String normalizeAppId(String raw) {
    final lower = raw.trim().toLowerCase();
    if (builtInSpecs.containsKey(lower)) return lower;
    if (lower == 'assistant' ||
        lower == 'ino' ||
        lower.startsWith('ino ') ||
        lower == 'ino.studio') {
      return 'ino';
    }
    if (lower.contains('aspire')) return 'aspire';
    if (lower == 'brain' || lower == 'brain graph') return 'brain';
    if (lower == 'workspace') return 'workspace';
    if (lower == 'file' || lower == 'files' || lower == 'file vault') {
      return 'files';
    }
    if (lower == 'task' || lower == 'tasks') return 'tasks';
    if (lower == 'trace' || lower == 'traces' || lower == 'system traces') {
      return 'trace';
    }
    if (lower.contains('markdraw') || lower.contains('excalidraw')) {
      return 'markdraw';
    }
    if (lower.contains('photocraft')) return 'photocraft';
    if (lower.contains('filmcraft') || lower.contains('videocraft')) {
      return 'filmcraft';
    }
    for (final key in builtInSpecs.keys) {
      if (key.endsWith('craft') && lower.contains(key)) return key;
    }
    if (lower == 'creator' || lower == 'studio') return 'creator';
    if (lower == 'gallery' || lower == 'ui gallery') return 'gallery';
    if (lower == 'friends' || lower == 'friend') return 'friends';
    if (lower == 'post' || lower == 'publish') return 'post';
    if (lower == 'setting' || lower == 'settings') return 'settings';
    if (lower == 'circle' ||
        lower == 'circles' ||
        lower == 'circleroom' ||
        lower == 'spaces' ||
        lower == 'marketplace') {
      return 'circles';
    }
    if (lower == 'calc' || lower == 'calculator') return 'calculator';
    if (lower == 'datavisualizer' || lower == 'data visualizer') {
      return 'datavisualizer';
    }
    if (lower == 'data' || lower == 'data studio') return 'data';
    return lower;
  }

  static const List<(Color, Color, IconData)> _dynamicPalettes = [
    (Color(0xFF10B981), Color(0xFF047857), LucideIcons.sparkles),
    (Color(0xFF6366F1), Color(0xFF3730A3), LucideIcons.blocks),
    (Color(0xFFF59E0B), Color(0xFFB45309), LucideIcons.zap),
    (Color(0xFF06B6D4), Color(0xFF0E7490), LucideIcons.terminal),
    (Color(0xFFEC4899), Color(0xFF9D174D), LucideIcons.rocket),
    (Color(0xFF8B5CF6), Color(0xFF5B21B6), LucideIcons.cpu),
    (Color(0xFF14B8A6), Color(0xFF0F766E), LucideIcons.compass),
    (Color(0xFF3B82F6), Color(0xFF1D4ED8), LucideIcons.globe),
  ];

  static String humanizeAppTitle(String raw) {
    final trimmed = raw.trim();
    if (trimmed.isEmpty) return 'App';
    if (trimmed.contains(' ') || trimmed.toLowerCase() != trimmed) {
      return trimmed;
    }
    if (trimmed == 'helloworld') return 'Hello World';
    if (trimmed == 'btcalert') return 'BTC Alert';
    if (trimmed.startsWith('btc') && trimmed.length > 3) {
      return 'BTC ${_capitalize(trimmed.substring(3))}';
    }
    final words = trimmed
        .split(RegExp(r'[-_.]+'))
        .where((s) => s.isNotEmpty)
        .map(_capitalize)
        .toList();
    return words.isEmpty ? _capitalize(trimmed) : words.join(' ');
  }

  static String _capitalize(String s) =>
      s.isEmpty ? s : '${s[0].toUpperCase()}${s.substring(1)}';

  static Color? tryParseHex(String? hex) {
    if (hex == null || hex.trim().isEmpty) return null;
    final clean = hex.trim().replaceAll('#', '');
    if (clean.length == 6) {
      final v = int.tryParse('FF$clean', radix: 16);
      if (v != null) return Color(v);
    } else if (clean.length == 8) {
      final v = int.tryParse(clean, radix: 16);
      if (v != null) return Color(v);
    }
    return null;
  }

  static IcAppIconSpec resolveSpec(
    String rawAppId, {
    IconData? iconOverride,
    String? accentHex,
    Color? topOverride,
    Color? bottomOverride,
  }) {
    final norm = normalizeAppId(rawAppId);
    final base = builtInSpecs[norm];
    final parsedAccent = tryParseHex(accentHex);

    if (base != null &&
        parsedAccent == null &&
        topOverride == null &&
        (iconOverride == null || iconOverride == base.icon)) {
      return base;
    }

    var hash = 0;
    for (final unit in norm.codeUnits) {
      hash = ((hash * 31) + unit) & 0x7fffffff;
    }
    final dynamicPalette = _dynamicPalettes[hash % _dynamicPalettes.length];
    final isBitcoinApp = norm.contains('btc') || norm.contains('bitcoin');

    final top =
        topOverride ??
        parsedAccent ??
        base?.gradientTop ??
        (isBitcoinApp ? const Color(0xFFF7931A) : dynamicPalette.$1);
    final bottom =
        bottomOverride ??
        (parsedAccent != null
            ? Color.lerp(parsedAccent, const Color(0xFF000000), 0.36)!
            : (base?.gradientBottom ??
                  (isBitcoinApp
                      ? const Color(0xFFB45309)
                      : dynamicPalette.$2)));

    return IcAppIconSpec(
      appId: norm,
      title: base?.title ?? humanizeAppTitle(rawAppId),
      category: base?.category ?? 'Micro-Apps',
      gradientTop: top,
      gradientBottom: bottom,
      icon:
          iconOverride ??
          base?.icon ??
          (isBitcoinApp ? LucideIcons.coins : dynamicPalette.$3),
      useInoStar:
          base?.useInoStar == true &&
          (iconOverride == null || iconOverride == base?.icon),
      brandGlyph: base?.brandGlyph,
      svgAsset:
          base?.svgAsset ?? (isBitcoinApp ? 'assets/brands/bitcoin.svg' : null),
      pngAsset: base?.pngAsset,
      isArtCraftBridge: base?.isArtCraftBridge == true,
      defaultWebPort: base?.defaultWebPort,
    );
  }

  @override
  Widget build(BuildContext context) {
    final spec = resolveSpec(
      appId,
      iconOverride: icon,
      accentHex: accentHex,
      topOverride: gradientTop,
      bottomOverride: gradientBottom,
    );
    final r = size * 0.22;
    final fg = glyphColor ?? spec.glyphColor;

    if (spec.pngAsset != null) {
      return Container(
        width: size,
        height: size,
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(r),
          boxShadow: [
            BoxShadow(
              color: const Color(0x38000000),
              blurRadius: size * 0.28,
              offset: Offset(0, size * 0.08),
            ),
          ],
        ),
        child: ClipRRect(
          borderRadius: BorderRadius.circular(r),
          child: Image.asset(
            spec.pngAsset!,
            width: size,
            height: size,
            fit: BoxFit.cover,
            filterQuality: FilterQuality.medium,
          ),
        ),
      );
    }

    Widget glyphWidget;
    if (spec.svgAsset != null) {
      glyphWidget = SvgPicture.asset(
        spec.svgAsset!,
        width: size * 0.64,
        height: size * 0.64,
      );
    } else if (spec.brandGlyph != null) {
      glyphWidget = CustomPaint(
        size: Size.square(size * 0.62),
        painter: _IcBrandGlyphPainter(
          glyph: spec.brandGlyph!,
          color: fg,
          accent: spec.gradientTop,
        ),
      );
    } else if (spec.useInoStar) {
      glyphWidget = CustomPaint(
        size: Size.square(size * 0.56),
        painter: _InoStarPainter(color: fg),
      );
    } else {
      glyphWidget = Icon(spec.icon, size: size * 0.52, color: fg);
    }

    return Container(
      width: size,
      height: size,
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(r),
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: [spec.gradientTop, spec.gradientBottom],
        ),
        border: Border.all(color: const Color(0x42FFFFFF), width: 1),
        boxShadow: [
          BoxShadow(
            color: const Color(0x38000000),
            blurRadius: size * 0.28,
            offset: Offset(0, size * 0.08),
          ),
        ],
      ),
      alignment: Alignment.center,
      child: glyphWidget,
    );
  }
}

class _IcBrandGlyphPainter extends CustomPainter {
  const _IcBrandGlyphPainter({
    required this.glyph,
    required this.color,
    required this.accent,
  });

  final IcBrandGlyph glyph;
  final Color color;
  final Color accent;

  @override
  void paint(Canvas canvas, Size size) {
    final w = size.width;
    final h = size.height;
    final cx = w / 2;
    final cy = h / 2;
    final stroke = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = math.max(1.5, w * 0.095)
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;
    final fill = Paint()
      ..color = color
      ..style = PaintingStyle.fill;
    final softFill = Paint()
      ..color = color.withValues(alpha: 0.30)
      ..style = PaintingStyle.fill;
    final badgeAccent = Paint()
      ..color = const Color(0xFFFFD166)
      ..style = PaintingStyle.fill;

    switch (glyph) {
      case IcBrandGlyph.inoStar:
        const _InoStarPainter(color: Color(0xFFFFFFFF)).paint(canvas, size);
        return;

      case IcBrandGlyph.aspireLogo:
        // Geometric faceted pyramid inspired by .NET Aspire icon
        final p1 = Path()
          ..moveTo(cx, h * 0.08)
          ..lineTo(w * 0.92, h * 0.86)
          ..lineTo(w * 0.08, h * 0.86)
          ..close();
        canvas.drawPath(p1, Paint()..color = const Color(0xFF7455DD));
        final p2 = Path()
          ..moveTo(cx, h * 0.32)
          ..lineTo(w * 0.76, h * 0.86)
          ..lineTo(w * 0.24, h * 0.86)
          ..close();
        canvas.drawPath(p2, Paint()..color = const Color(0xFFB9AAEE));
        final p3 = Path()
          ..moveTo(cx * 0.72, h * 0.56)
          ..lineTo(cx * 1.15, h * 0.86)
          ..lineTo(w * 0.14, h * 0.86)
          ..close();
        canvas.drawPath(p3, Paint()..color = const Color(0xFFDCD5F6));
        return;

      case IcBrandGlyph.brainGraph:
        // 3D Neural constellation with glowing core and synaptic links
        final nodes = <Offset>[
          Offset(cx, cy),
          Offset(w * 0.20, h * 0.24),
          Offset(w * 0.82, h * 0.26),
          Offset(w * 0.22, h * 0.78),
          Offset(w * 0.80, h * 0.76),
          Offset(cx, h * 0.12),
        ];
        final linkPaint = Paint()
          ..color = color.withValues(alpha: 0.55)
          ..strokeWidth = math.max(1.2, w * 0.065);
        for (var i = 1; i < nodes.length; i++) {
          canvas.drawLine(nodes[0], nodes[i], linkPaint);
        }
        canvas.drawLine(nodes[1], nodes[5], linkPaint);
        canvas.drawLine(nodes[2], nodes[5], linkPaint);
        canvas.drawLine(nodes[1], nodes[3], linkPaint);
        canvas.drawLine(nodes[2], nodes[4], linkPaint);
        canvas.drawCircle(nodes[0], w * 0.17, fill);
        for (var i = 1; i < nodes.length; i++) {
          canvas.drawCircle(nodes[i], w * 0.095, i == 5 ? badgeAccent : fill);
        }
        return;

      case IcBrandGlyph.workspaceCanvas:
        // Infinite spatial canvas grid + floating window cards
        final r1 = RRect.fromRectAndRadius(
          Rect.fromLTWH(w * 0.08, h * 0.14, w * 0.52, h * 0.42),
          Radius.circular(w * 0.10),
        );
        final r2 = RRect.fromRectAndRadius(
          Rect.fromLTWH(w * 0.40, h * 0.44, w * 0.52, h * 0.42),
          Radius.circular(w * 0.10),
        );
        canvas.drawRRect(r1, softFill);
        canvas.drawRRect(r1, stroke);
        canvas.drawRRect(r2, fill);
        canvas.drawCircle(Offset(w * 0.78, h * 0.22), w * 0.08, badgeAccent);
        return;

      case IcBrandGlyph.filesVault:
        final folder = Path()
          ..moveTo(w * 0.10, h * 0.24)
          ..lineTo(w * 0.42, h * 0.24)
          ..lineTo(w * 0.52, h * 0.36)
          ..lineTo(w * 0.90, h * 0.36)
          ..lineTo(w * 0.90, h * 0.82)
          ..lineTo(w * 0.10, h * 0.82)
          ..close();
        canvas.drawPath(folder, softFill);
        canvas.drawPath(folder, stroke);
        canvas.drawLine(
          Offset(w * 0.26, h * 0.58),
          Offset(w * 0.74, h * 0.58),
          stroke,
        );
        return;

      case IcBrandGlyph.dataStudio:
        // Rising analytics bars + spline curve
        canvas.drawRRect(
          RRect.fromRectAndRadius(
            Rect.fromLTWH(w * 0.14, h * 0.54, w * 0.16, h * 0.34),
            Radius.circular(w * 0.04),
          ),
          softFill,
        );
        canvas.drawRRect(
          RRect.fromRectAndRadius(
            Rect.fromLTWH(w * 0.42, h * 0.38, w * 0.16, h * 0.50),
            Radius.circular(w * 0.04),
          ),
          softFill,
        );
        canvas.drawRRect(
          RRect.fromRectAndRadius(
            Rect.fromLTWH(w * 0.70, h * 0.22, w * 0.16, h * 0.66),
            Radius.circular(w * 0.04),
          ),
          fill,
        );
        final curve = Path()
          ..moveTo(w * 0.14, h * 0.42)
          ..quadraticBezierTo(w * 0.45, h * 0.12, w * 0.84, h * 0.16);
        canvas.drawPath(curve, stroke);
        return;

      case IcBrandGlyph.markdrawCanvas:
        // Freehand vector nib + diagram node link
        canvas.drawRRect(
          RRect.fromRectAndRadius(
            Rect.fromLTWH(w * 0.10, h * 0.14, w * 0.36, h * 0.28),
            Radius.circular(w * 0.06),
          ),
          stroke,
        );
        canvas.drawCircle(Offset(w * 0.74, h * 0.72), w * 0.18, fill);
        canvas.drawLine(
          Offset(w * 0.46, h * 0.28),
          Offset(w * 0.60, h * 0.58),
          stroke,
        );
        return;

      case IcBrandGlyph.creatorLoop:
        // Self-improving infinity / loop + spark
        canvas.drawArc(
          Rect.fromCircle(center: Offset(cx, cy), radius: w * 0.36),
          -math.pi * 0.8,
          math.pi * 1.45,
          false,
          stroke,
        );
        canvas.drawCircle(Offset(cx, cy), w * 0.14, fill);
        canvas.drawCircle(Offset(w * 0.80, h * 0.22), w * 0.10, badgeAccent);
        return;

      case IcBrandGlyph.uiGallery:
        // 4-quadrant swatch grid
        final g = w * 0.34;
        canvas.drawRRect(
          RRect.fromRectAndRadius(
            Rect.fromLTWH(w * 0.12, h * 0.12, g, g),
            Radius.circular(w * 0.08),
          ),
          fill,
        );
        canvas.drawRRect(
          RRect.fromRectAndRadius(
            Rect.fromLTWH(w * 0.54, h * 0.12, g, g),
            Radius.circular(w * 0.08),
          ),
          badgeAccent,
        );
        canvas.drawRRect(
          RRect.fromRectAndRadius(
            Rect.fromLTWH(w * 0.12, h * 0.54, g, g),
            Radius.circular(w * 0.08),
          ),
          softFill,
        );
        canvas.drawRRect(
          RRect.fromRectAndRadius(
            Rect.fromLTWH(w * 0.54, h * 0.54, g, g),
            Radius.circular(w * 0.08),
          ),
          stroke,
        );
        return;

      case IcBrandGlyph.tasksBoard:
        final check = Path()
          ..moveTo(w * 0.18, h * 0.52)
          ..lineTo(w * 0.40, h * 0.74)
          ..lineTo(w * 0.84, h * 0.26);
        canvas.drawPath(check, stroke..strokeWidth = math.max(2.0, w * 0.12));
        return;

      case IcBrandGlyph.traceTelemetry:
        final pulse = Path()
          ..moveTo(w * 0.08, cy)
          ..lineTo(w * 0.30, cy)
          ..lineTo(w * 0.44, h * 0.18)
          ..lineTo(w * 0.60, h * 0.82)
          ..lineTo(w * 0.72, cy)
          ..lineTo(w * 0.92, cy);
        canvas.drawPath(pulse, stroke);
        return;

      case IcBrandGlyph.postReach:
        final plane = Path()
          ..moveTo(w * 0.12, h * 0.46)
          ..lineTo(w * 0.88, h * 0.16)
          ..lineTo(w * 0.62, h * 0.86)
          ..lineTo(w * 0.44, h * 0.58)
          ..close();
        canvas.drawPath(plane, softFill);
        canvas.drawPath(plane, stroke);
        return;

      case IcBrandGlyph.circlesSocial:
        canvas.drawCircle(Offset(w * 0.38, cy), w * 0.26, stroke);
        canvas.drawCircle(Offset(w * 0.64, cy), w * 0.26, softFill);
        canvas.drawCircle(Offset(w * 0.64, cy), w * 0.26, stroke);
        return;

      case IcBrandGlyph.calculatorGrid:
        canvas.drawRRect(
          RRect.fromRectAndRadius(
            Rect.fromLTWH(w * 0.16, h * 0.12, w * 0.68, h * 0.76),
            Radius.circular(w * 0.10),
          ),
          stroke,
        );
        canvas.drawLine(
          Offset(w * 0.30, h * 0.32),
          Offset(w * 0.70, h * 0.32),
          stroke,
        );
        canvas.drawCircle(Offset(w * 0.36, h * 0.56), w * 0.06, fill);
        canvas.drawCircle(Offset(w * 0.64, h * 0.56), w * 0.06, fill);
        canvas.drawCircle(Offset(w * 0.36, h * 0.72), w * 0.06, fill);
        canvas.drawCircle(Offset(w * 0.64, h * 0.72), w * 0.06, badgeAccent);
        return;

      case IcBrandGlyph.settingsGear:
        canvas.drawCircle(Offset(cx, cy), w * 0.22, stroke);
        for (var i = 0; i < 6; i++) {
          final a = i * math.pi / 3;
          canvas.drawLine(
            Offset(cx + math.cos(a) * w * 0.26, cy + math.sin(a) * h * 0.26),
            Offset(cx + math.cos(a) * w * 0.40, cy + math.sin(a) * h * 0.40),
            stroke,
          );
        }
        return;

      case IcBrandGlyph.solidcraftCube:
        // Isometric 3D cube
        final top = Path()
          ..moveTo(cx, h * 0.12)
          ..lineTo(w * 0.86, h * 0.32)
          ..lineTo(cx, h * 0.52)
          ..lineTo(w * 0.14, h * 0.32)
          ..close();
        canvas.drawPath(top, fill);
        canvas.drawPath(top, stroke);
        final left = Path()
          ..moveTo(w * 0.14, h * 0.32)
          ..lineTo(cx, h * 0.52)
          ..lineTo(cx, h * 0.88)
          ..lineTo(w * 0.14, h * 0.68)
          ..close();
        canvas.drawPath(left, softFill);
        canvas.drawPath(left, stroke);
        final right = Path()
          ..moveTo(w * 0.86, h * 0.32)
          ..lineTo(cx, h * 0.52)
          ..lineTo(cx, h * 0.88)
          ..lineTo(w * 0.86, h * 0.68)
          ..close();
        canvas.drawPath(right, stroke);
        return;
    }
  }

  @override
  bool shouldRepaint(covariant _IcBrandGlyphPainter old) =>
      old.glyph != glyph || old.color != color || old.accent != accent;
}

class _InoStarPainter extends CustomPainter {
  const _InoStarPainter({required this.color});
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    final cx = size.width / 2;
    final cy = size.height / 2;
    final r = size.width / 2;
    final inner = r * 0.26;
    final path = Path();
    for (int i = 0; i < 8; i++) {
      final a = i * math.pi / 4 - math.pi / 2;
      final rad = i.isEven ? r : inner;
      final x = cx + math.cos(a) * rad;
      final y = cy + math.sin(a) * rad;
      if (i == 0) {
        path.moveTo(x, y);
      } else {
        path.lineTo(x, y);
      }
    }
    path.close();
    canvas.drawPath(path, Paint()..color = color);
    canvas.drawCircle(
      Offset(cx + r * 0.58, cy - r * 0.58),
      r * 0.13,
      Paint()..color = color.withValues(alpha: 0.85),
    );
  }

  @override
  bool shouldRepaint(covariant _InoStarPainter old) => old.color != color;
}

/// Shared app icon tile used in the desktop launcher, dock, and gallery.
class AppTile extends StatelessWidget {
  const AppTile({
    super.key,
    this.icon,
    this.accent,
    this.size = 44,
    this.appId,
    this.accentHex,
    this.style = AppTileStyle.brand,
    this.brandColor,
    this.glyph,
  });

  final IconData? icon;
  final Color? accent;
  final double size;
  final String? appId;
  final String? accentHex;
  final AppTileStyle style;
  final Color? brandColor;
  final Widget? glyph;

  @override
  Widget build(BuildContext context) {
    if (appId != null) {
      return IcAppIcon(
        appId: appId!,
        icon: icon,
        accentHex: accentHex,
        size: size,
        gradientTop: brandColor ?? accent,
      );
    }
    final c = IcTheme.of(context);
    final effectiveAccent = brandColor ?? accent ?? c.accent;
    final r = size * 0.22;
    return Container(
      width: size,
      height: size,
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(r),
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: style == AppTileStyle.brand
              ? [
                  effectiveAccent,
                  Color.lerp(effectiveAccent, const Color(0xFF000000), 0.36)!,
                ]
              : [
                  effectiveAccent.withValues(alpha: c.isDark ? 0.28 : 0.18),
                  effectiveAccent.withValues(alpha: c.isDark ? 0.10 : 0.06),
                ],
        ),
        border: Border.all(
          color: style == AppTileStyle.brand
              ? const Color(0x38FFFFFF)
              : effectiveAccent.withValues(alpha: 0.45),
        ),
        boxShadow: [
          BoxShadow(
            color: effectiveAccent.withValues(alpha: c.isDark ? 0.22 : 0.12),
            blurRadius: size * 0.28,
            offset: Offset(0, size * 0.08),
          ),
        ],
      ),
      alignment: Alignment.center,
      child:
          glyph ??
          Icon(
            icon ?? LucideIcons.layoutGrid,
            size: size * 0.50,
            color: style == AppTileStyle.brand
                ? const Color(0xFFFFFFFF)
                : effectiveAccent,
          ),
    );
  }
}
