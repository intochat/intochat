import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/tokens.dart';

/// Immutable allowlisted visual descriptor bound from C# `VisualCatalog` node fields.
///
/// Security invariant: [pngAsset] and [svgAsset] are strictly populated from
/// compile-time allowlisted local asset paths under `assets/brands/` — never
/// from arbitrary external URLs or untrusted wire paths.
class BrandVisualSpec {
  const BrandVisualSpec({
    required this.key,
    required this.label,
    this.accent,
    this.pngAsset,
    this.svgAsset,
    this.fallbackIcon = LucideIcons.sparkles,
    this.monochromeSvg = false,
  });

  final String key;
  final String label;
  final Color? accent;
  final String? pngAsset;
  final String? svgAsset;
  final IconData fallbackIcon;
  final bool monochromeSvg;

  bool get hasAsset => pngAsset != null || svgAsset != null;
}

/// Asset-path-only registry keyed by canonical C# `VisualCatalog` icon keys
/// (`brand:*`, `provider:*`, `capability:*`, `artcraft:*`, `app:*`).
///
/// Brand labels, model display formatting, and semantic categories come exclusively
/// from C# (`VisualCatalog`) on the wire node — never duplicated in Dart.
abstract final class BrandVisuals {
  static const Map<String, String> pngAssetPaths = {
    'brand:aspire': 'assets/brands/aspire.png',
    'artcraft:artcraft': 'assets/brands/artcraft/artcraft.png',
    'artcraft:photocraft': 'assets/brands/artcraft/photocraft.png',
    'artcraft:filmcraft': 'assets/brands/artcraft/filmcraft.png',
    'artcraft:vectorcraft': 'assets/brands/artcraft/vectorcraft.png',
    'artcraft:lightcraft': 'assets/brands/artcraft/lightcraft.png',
    'artcraft:effectcraft': 'assets/brands/artcraft/effectcraft.png',
    'artcraft:designcraft': 'assets/brands/artcraft/designcraft.png',
    'artcraft:pdfcraft': 'assets/brands/artcraft/pdfcraft.png',
    'artcraft:wordcraft': 'assets/brands/artcraft/wordcraft.png',
    'artcraft:soundcraft': 'assets/brands/artcraft/soundcraft.png',
    'artcraft:deckcraft': 'assets/brands/artcraft/deckcraft.png',
    'artcraft:cadcraft': 'assets/brands/artcraft/cadcraft.png',
    'artcraft:gridcraft': 'assets/brands/artcraft/gridcraft.png',
    'artcraft:figcraft': 'assets/brands/artcraft/figcraft.png',
    'artcraft:texturecraft': 'assets/brands/artcraft/texturecraft.png',
    'artcraft:materialcraft': 'assets/brands/artcraft/materialcraft.png',
    'artcraft:solidcraft': 'assets/brands/artcraft/solidcraft.png',
  };

  static const Map<String, String> svgAssetPaths = {
    'provider:anthropic': 'assets/brands/providers/anthropic.svg',
    'provider:openai': 'assets/brands/providers/openai.svg',
    'provider:google': 'assets/brands/providers/google.svg',
    'provider:ollama': 'assets/brands/providers/ollama.svg',
    'provider:qwen': 'assets/brands/providers/qwen.svg',
    'provider:xai': 'assets/brands/providers/xai.svg',
    'provider:openrouter': 'assets/brands/providers/openrouter.svg',
    'provider:foundry': 'assets/brands/providers/foundry.svg',
    'brand:aspire': 'assets/brands/aspire-icon-32.svg',
    'brand:github': 'assets/brands/github.svg',
    'brand:gmail': 'assets/brands/gmail.svg',
    'brand:salesforce': 'assets/brands/salesforce.svg',
    'brand:intochat': 'assets/brands/ic-mark.svg',
    'brand:docker': 'assets/brands/docker.svg',
    'brand:dotnet': 'assets/brands/dotnet.svg',
    'brand:bitcoin': 'assets/brands/bitcoin.svg',
  };

  static const Set<String> monochromeSvgKeys = {
    'provider:openai',
    'provider:ollama',
    'provider:xai',
    'brand:github',
    'brand:intochat',
  };

  static const Map<String, IconData> capabilityFallbackIcons = {
    'capability:memory': LucideIcons.database,
    'capability:codegraph': LucideIcons.gitBranch,
    'capability:traces': LucideIcons.activity,
    'capability:http': LucideIcons.globe,
    'capability:notifications': LucideIcons.bell,
    'capability:shield': LucideIcons.shieldAlert,
    'capability:files': LucideIcons.folderOpen,
    'capability:studio': LucideIcons.sparkles,
    'app:ino': LucideIcons.sparkles,
    'app:settings': LucideIcons.settings,
    'app:files': LucideIcons.folderOpen,
    'app:gallery': LucideIcons.layoutGrid,
    'app:creator': LucideIcons.cpu,
    'app:desktop': LucideIcons.monitor,
    'app:workspace': LucideIcons.layers,
    'app:tasks': LucideIcons.checkSquare,
    'app:calculator': LucideIcons.calculator,
    'app:data': LucideIcons.barChart2,
    'app:usage': LucideIcons.gauge,
    'app:friends': LucideIcons.users,
    'app:circles': LucideIcons.circleDot,
    'app:post': LucideIcons.send,
    'app:markdraw': LucideIcons.penTool,
    'app:brain': LucideIcons.brain,
  };

  /// Looks up a bundled asset or capability glyph by canonical C# `iconKey`.
  /// Returns `null` for unrecognized or untrusted external keys.
  static BrandVisualSpec? tryForIconKey(
    String? iconKey, {
    String? label,
    String? accentHex,
  }) {
    final key = (iconKey ?? '').trim().toLowerCase();
    if (key.isEmpty) return null;

    final png = pngAssetPaths[key];
    final svg = png == null ? svgAssetPaths[key] : null;
    final fallback = capabilityFallbackIcons[key];
    if (png == null && svg == null && fallback == null) return null;

    return BrandVisualSpec(
      key: key,
      label: (label ?? '').trim(),
      accent: _tryParseHexColor(accentHex),
      pngAsset: png,
      svgAsset: svg,
      fallbackIcon: fallback ?? LucideIcons.sparkles,
      monochromeSvg: monochromeSvgKeys.contains(key),
    );
  }

  /// Constructs a display [BrandVisualSpec] directly from C#-emitted node label,
  /// optional canonical [iconKey], and optional [accentHex].
  static BrandVisualSpec fromNode({
    required String? label,
    String? iconKey,
    String? accentHex,
    IconData fallbackIcon = LucideIcons.sparkles,
  }) {
    final resolved = tryForIconKey(iconKey, label: label, accentHex: accentHex);
    if (resolved != null) return resolved;

    return BrandVisualSpec(
      key: (iconKey ?? '').trim(),
      label: (label ?? '').trim(),
      accent: _tryParseHexColor(accentHex),
      fallbackIcon: fallbackIcon,
    );
  }

  static Color? _tryParseHexColor(String? hex) {
    final raw = (hex ?? '').trim().replaceAll('#', '');
    if (raw.length == 6) {
      final parsed = int.tryParse('FF$raw', radix: 16);
      if (parsed != null) return Color(parsed);
    } else if (raw.length == 8) {
      final parsed = int.tryParse(raw, radix: 16);
      if (parsed != null) return Color(parsed);
    }
    return null;
  }
}

/// Renders a crisp brand/provider/capability icon from an allowlisted
/// [BrandVisualSpec] using bundled PNG (`Image.asset`), SVG (`SvgPicture.asset`),
/// or vector fallback (`Icon`).
class IcBrandIcon extends StatelessWidget {
  const IcBrandIcon({super.key, required this.spec, this.size = 16});

  final BrandVisualSpec spec;
  final double size;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    if (spec.pngAsset != null) {
      return SizedBox(
        width: size,
        height: size,
        child: Image.asset(
          spec.pngAsset!,
          width: size,
          height: size,
          fit: BoxFit.contain,
          filterQuality: FilterQuality.medium,
          errorBuilder: (_, _, _) => _buildSvgOrFallback(c),
        ),
      );
    }
    return _buildSvgOrFallback(c);
  }

  Widget _buildSvgOrFallback(IcColors c) {
    if (spec.svgAsset != null) {
      return SizedBox(
        width: size,
        height: size,
        child: SvgPicture.asset(
          spec.svgAsset!,
          width: size,
          height: size,
          fit: BoxFit.contain,
          colorFilter: spec.monochromeSvg
              ? ColorFilter.mode(c.textStrong, BlendMode.srcIn)
              : null,
        ),
      );
    }
    return Icon(spec.fallbackIcon, size: size, color: spec.accent ?? c.accent);
  }
}

/// Compact pill displaying a [BrandVisualSpec] icon + human-readable label.
class IcBrandChip extends StatelessWidget {
  const IcBrandChip({
    super.key,
    required this.spec,
    this.labelOverride,
    this.iconSize = 13,
    this.compact = false,
  });

  final BrandVisualSpec spec;
  final String? labelOverride;
  final double iconSize;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final label = labelOverride ?? spec.label;
    final tint = spec.accent ?? c.accent;

    return Container(
      padding: EdgeInsets.symmetric(
        horizontal: compact ? 6 : 8,
        vertical: compact ? 2 : 3,
      ),
      decoration: BoxDecoration(
        color: Color.alphaBlend(
          tint.withValues(alpha: c.isDark ? 0.12 : 0.08),
          c.surfaceSunk,
        ),
        borderRadius: BorderRadius.circular(999),
        border: Border.all(
          color: tint.withValues(alpha: c.isDark ? 0.28 : 0.22),
        ),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          IcBrandIcon(spec: spec, size: iconSize),
          const SizedBox(width: 5),
          Flexible(
            child: Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: IcType.text(
                compact ? 10.5 : 11,
                weight: FontWeight.w600,
                color: c.textStrong,
                height: 1.2,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Alias for [IcBrandChip] so callers can refer to either `IcBrandBadge` or `IcBrandChip`.
typedef IcBrandBadge = IcBrandChip;
