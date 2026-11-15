import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/tokens.dart';

/// Immutable allowlisted visual descriptor for an AI model, provider, brand,
/// capability, or ArtCraft studio application.
///
/// Security invariant: [pngAsset] and [svgAsset] are strictly populated from
/// compile-time allowlisted local asset paths under `assets/brands/` — never
/// from arbitrary external URLs or untrusted wire paths.
class BrandVisualSpec {
  const BrandVisualSpec({
    required this.key,
    required this.label,
    required this.accent,
    this.category = 'System',
    this.pngAsset,
    this.svgAsset,
    this.fallbackIcon = LucideIcons.sparkles,
    this.monochromeSvg = false,
  });

  final String key;
  final String label;
  final Color accent;
  final String category;
  final String? pngAsset;
  final String? svgAsset;
  final IconData fallbackIcon;
  final bool monochromeSvg;

  bool get hasAsset => pngAsset != null || svgAsset != null;

  BrandVisualSpec copyWith({String? label, String? category, Color? accent}) {
    return BrandVisualSpec(
      key: key,
      label: label ?? this.label,
      accent: accent ?? this.accent,
      category: category ?? this.category,
      pngAsset: pngAsset,
      svgAsset: svgAsset,
      fallbackIcon: fallbackIcon,
      monochromeSvg: monochromeSvg,
    );
  }
}

/// Pure allowlisted local visual resolver shared across Chat, Approvals,
/// Activity rows, Dock, All Apps Launcher, Window frames, and Command Palette.
abstract final class BrandVisuals {
  static const Map<String, BrandVisualSpec> providers = {
    'provider:anthropic': BrandVisualSpec(
      key: 'provider:anthropic',
      label: 'Anthropic',
      category: 'Provider',
      accent: Color(0xFFD97757),
      svgAsset: 'assets/brands/providers/anthropic.svg',
      fallbackIcon: LucideIcons.sparkles,
    ),
    'provider:openai': BrandVisualSpec(
      key: 'provider:openai',
      label: 'OpenAI',
      category: 'Provider',
      accent: Color(0xFF10A37F),
      svgAsset: 'assets/brands/providers/openai.svg',
      fallbackIcon: LucideIcons.bot,
    ),
    'provider:google': BrandVisualSpec(
      key: 'provider:google',
      label: 'Google',
      category: 'Provider',
      accent: Color(0xFF4285F4),
      svgAsset: 'assets/brands/providers/google.svg',
      fallbackIcon: LucideIcons.sparkles,
    ),
    'provider:ollama': BrandVisualSpec(
      key: 'provider:ollama',
      label: 'Ollama',
      category: 'Provider',
      accent: Color(0xFF6366F1),
      svgAsset: 'assets/brands/providers/ollama.svg',
      fallbackIcon: LucideIcons.cpu,
    ),
    'provider:qwen': BrandVisualSpec(
      key: 'provider:qwen',
      label: 'Qwen',
      category: 'Provider',
      accent: Color(0xFF7C3AED),
      svgAsset: 'assets/brands/providers/qwen.svg',
      fallbackIcon: LucideIcons.cpu,
    ),
    'provider:xai': BrandVisualSpec(
      key: 'provider:xai',
      label: 'xAI',
      category: 'Provider',
      accent: Color(0xFF94A3B8),
      svgAsset: 'assets/brands/providers/xai.svg',
      fallbackIcon: LucideIcons.zap,
    ),
    'provider:openrouter': BrandVisualSpec(
      key: 'provider:openrouter',
      label: 'OpenRouter',
      category: 'Provider',
      accent: Color(0xFF6366F1),
      svgAsset: 'assets/brands/providers/openrouter.svg',
      fallbackIcon: LucideIcons.gitBranch,
    ),
    'provider:foundry': BrandVisualSpec(
      key: 'provider:foundry',
      label: 'Microsoft Foundry',
      category: 'Provider',
      accent: Color(0xFF0078D4),
      svgAsset: 'assets/brands/providers/foundry.svg',
      fallbackIcon: LucideIcons.boxes,
    ),
  };

  static const Map<String, BrandVisualSpec> catalog = {
    // Brands
    'brand:aspire': BrandVisualSpec(
      key: 'brand:aspire',
      label: 'Aspire',
      category: 'Developer',
      accent: Color(0xFF512BD4),
      pngAsset: 'assets/brands/aspire.png',
      svgAsset: 'assets/brands/aspire-icon-32.svg',
      fallbackIcon: LucideIcons.layers,
    ),
    'brand:github': BrandVisualSpec(
      key: 'brand:github',
      label: 'GitHub',
      category: 'Developer',
      accent: Color(0xFF6E7681),
      svgAsset: 'assets/brands/github.svg',
      fallbackIcon: LucideIcons.gitBranch,
      monochromeSvg: true,
    ),
    'brand:gmail': BrandVisualSpec(
      key: 'brand:gmail',
      label: 'Gmail',
      category: 'Productivity',
      accent: Color(0xFFEA4335),
      svgAsset: 'assets/brands/gmail.svg',
      fallbackIcon: LucideIcons.mail,
    ),
    'brand:salesforce': BrandVisualSpec(
      key: 'brand:salesforce',
      label: 'Salesforce',
      category: 'Enterprise',
      accent: Color(0xFF00A1E0),
      svgAsset: 'assets/brands/salesforce.svg',
      fallbackIcon: LucideIcons.cloud,
    ),
    'brand:intochat': BrandVisualSpec(
      key: 'brand:intochat',
      label: 'Ino Studio',
      category: 'Intelligence',
      accent: Color(0xFF6366F1),
      svgAsset: 'assets/brands/ic-mark.svg',
      fallbackIcon: LucideIcons.sparkles,
    ),
    'brand:docker': BrandVisualSpec(
      key: 'brand:docker',
      label: 'Docker',
      category: 'Developer',
      accent: Color(0xFF2496ED),
      svgAsset: 'assets/brands/docker.svg',
      fallbackIcon: LucideIcons.box,
    ),
    'brand:dotnet': BrandVisualSpec(
      key: 'brand:dotnet',
      label: '.NET',
      category: 'Developer',
      accent: Color(0xFF512BD4),
      svgAsset: 'assets/brands/dotnet.svg',
      fallbackIcon: LucideIcons.code,
    ),
    'brand:bitcoin': BrandVisualSpec(
      key: 'brand:bitcoin',
      label: 'Bitcoin',
      category: 'Finance',
      accent: Color(0xFFF7931A),
      svgAsset: 'assets/brands/bitcoin.svg',
      fallbackIcon: LucideIcons.coins,
    ),

    // Capabilities
    'capability:memory': BrandVisualSpec(
      key: 'capability:memory',
      label: 'Memory',
      category: 'Intelligence',
      accent: Color(0xFF8B5CF6),
      fallbackIcon: LucideIcons.brain,
    ),
    'capability:codegraph': BrandVisualSpec(
      key: 'capability:codegraph',
      label: 'CodeGraph',
      category: 'Developer',
      accent: Color(0xFF10B981),
      fallbackIcon: LucideIcons.gitFork,
    ),
    'capability:traces': BrandVisualSpec(
      key: 'capability:traces',
      label: 'System Traces',
      category: 'Analytics',
      accent: Color(0xFFF59E0B),
      fallbackIcon: LucideIcons.activity,
    ),
    'capability:http': BrandVisualSpec(
      key: 'capability:http',
      label: 'Web HTTPS',
      category: 'Network',
      accent: Color(0xFF0EA5E9),
      fallbackIcon: LucideIcons.globe,
    ),
    'capability:notifications': BrandVisualSpec(
      key: 'capability:notifications',
      label: 'Notifications',
      category: 'System',
      accent: Color(0xFFEC4899),
      fallbackIcon: LucideIcons.bell,
    ),
    'capability:shield': BrandVisualSpec(
      key: 'capability:shield',
      label: 'Human Approval',
      category: 'Security',
      accent: Color(0xFFF97316),
      fallbackIcon: LucideIcons.shieldCheck,
    ),
    'capability:files': BrandVisualSpec(
      key: 'capability:files',
      label: 'Files',
      category: 'Workspace',
      accent: Color(0xFFF6B73C),
      fallbackIcon: LucideIcons.folder,
    ),
    'capability:studio': BrandVisualSpec(
      key: 'capability:studio',
      label: 'Studio',
      category: 'Studio',
      accent: Color(0xFFFF5D8F),
      fallbackIcon: LucideIcons.wandSparkles,
    ),

    // OS Applications
    'app:ino': BrandVisualSpec(
      key: 'app:ino',
      label: 'Ino',
      category: 'Intelligence',
      accent: Color(0xFF1F5EFF),
      svgAsset: 'assets/brands/ic-mark.svg',
      fallbackIcon: LucideIcons.sparkles,
    ),
    'app:settings': BrandVisualSpec(
      key: 'app:settings',
      label: 'Settings',
      category: 'System',
      accent: Color(0xFF64748B),
      fallbackIcon: LucideIcons.slidersHorizontal,
    ),
    'app:files': BrandVisualSpec(
      key: 'app:files',
      label: 'Files',
      category: 'Workspace',
      accent: Color(0xFFF6B73C),
      fallbackIcon: LucideIcons.folder,
    ),
    'app:gallery': BrandVisualSpec(
      key: 'app:gallery',
      label: 'UI Gallery',
      category: 'Studio',
      accent: Color(0xFF22C55E),
      fallbackIcon: LucideIcons.palette,
    ),
    'app:creator': BrandVisualSpec(
      key: 'app:creator',
      label: 'Creator',
      category: 'Studio',
      accent: Color(0xFFFF5D8F),
      fallbackIcon: LucideIcons.wandSparkles,
    ),
    'app:desktop': BrandVisualSpec(
      key: 'app:desktop',
      label: 'Desktop',
      category: 'Workspace',
      accent: Color(0xFF1F5EFF),
      fallbackIcon: LucideIcons.layoutGrid,
    ),
    'app:workspace': BrandVisualSpec(
      key: 'app:workspace',
      label: 'Workspace',
      category: 'Workspace',
      accent: Color(0xFF14B8A6),
      fallbackIcon: LucideIcons.compass,
    ),
    'app:tasks': BrandVisualSpec(
      key: 'app:tasks',
      label: 'Tasks',
      category: 'Workspace',
      accent: Color(0xFF8B5CF6),
      fallbackIcon: LucideIcons.listTodo,
    ),
    'app:calculator': BrandVisualSpec(
      key: 'app:calculator',
      label: 'Calculator',
      category: 'Utilities',
      accent: Color(0xFF14B8A6),
      fallbackIcon: LucideIcons.calculator,
    ),
    'app:data': BrandVisualSpec(
      key: 'app:data',
      label: 'Data Studio',
      category: 'Analytics',
      accent: Color(0xFF38BDF8),
      fallbackIcon: LucideIcons.chartSpline,
    ),
    'app:usage': BrandVisualSpec(
      key: 'app:usage',
      label: 'Usage',
      category: 'Analytics',
      accent: Color(0xFF0EA5E9),
      fallbackIcon: LucideIcons.gauge,
    ),
    'app:friends': BrandVisualSpec(
      key: 'app:friends',
      label: 'Friends',
      category: 'Social',
      accent: Color(0xFFFB7185),
      fallbackIcon: LucideIcons.users,
    ),
    'app:circles': BrandVisualSpec(
      key: 'app:circles',
      label: 'Circles',
      category: 'Social',
      accent: Color(0xFFA855F7),
      fallbackIcon: LucideIcons.circleDot,
    ),
    'app:post': BrandVisualSpec(
      key: 'app:post',
      label: 'Post',
      category: 'Reach',
      accent: Color(0xFFFB7185),
      fallbackIcon: LucideIcons.megaphone,
    ),
    'app:markdraw': BrandVisualSpec(
      key: 'app:markdraw',
      label: 'Markdraw',
      category: 'Studio',
      accent: Color(0xFF10B981),
      fallbackIcon: LucideIcons.penTool,
    ),
    'app:brain': BrandVisualSpec(
      key: 'app:brain',
      label: 'Brain',
      category: 'Intelligence',
      accent: Color(0xFF6366F1),
      fallbackIcon: LucideIcons.brainCircuit,
    ),

    // All 17 ArtCraft Studios
    'artcraft:artcraft': BrandVisualSpec(
      key: 'artcraft:artcraft',
      label: 'ArtCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFFEC4899),
      pngAsset: 'assets/brands/artcraft/artcraft.png',
      fallbackIcon: LucideIcons.palette,
    ),
    'artcraft:photocraft': BrandVisualSpec(
      key: 'artcraft:photocraft',
      label: 'PhotoCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFF3B82F6),
      pngAsset: 'assets/brands/artcraft/photocraft.png',
      fallbackIcon: LucideIcons.aperture,
    ),
    'artcraft:filmcraft': BrandVisualSpec(
      key: 'artcraft:filmcraft',
      label: 'FilmCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFFA855F7),
      pngAsset: 'assets/brands/artcraft/filmcraft.png',
      fallbackIcon: LucideIcons.clapperboard,
    ),
    'artcraft:vectorcraft': BrandVisualSpec(
      key: 'artcraft:vectorcraft',
      label: 'VectorCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFFF97316),
      pngAsset: 'assets/brands/artcraft/vectorcraft.png',
      fallbackIcon: LucideIcons.penTool,
    ),
    'artcraft:lightcraft': BrandVisualSpec(
      key: 'artcraft:lightcraft',
      label: 'LightCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFF0EA5E9),
      pngAsset: 'assets/brands/artcraft/lightcraft.png',
      fallbackIcon: LucideIcons.slidersHorizontal,
    ),
    'artcraft:effectcraft': BrandVisualSpec(
      key: 'artcraft:effectcraft',
      label: 'EffectCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFF8B5CF6),
      pngAsset: 'assets/brands/artcraft/effectcraft.png',
      fallbackIcon: LucideIcons.sparkles,
    ),
    'artcraft:designcraft': BrandVisualSpec(
      key: 'artcraft:designcraft',
      label: 'DesignCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFFF43F5E),
      pngAsset: 'assets/brands/artcraft/designcraft.png',
      fallbackIcon: LucideIcons.layoutTemplate,
    ),
    'artcraft:pdfcraft': BrandVisualSpec(
      key: 'artcraft:pdfcraft',
      label: 'PDFCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFFEF4444),
      pngAsset: 'assets/brands/artcraft/pdfcraft.png',
      fallbackIcon: LucideIcons.fileText,
    ),
    'artcraft:wordcraft': BrandVisualSpec(
      key: 'artcraft:wordcraft',
      label: 'WordCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFF2563EB),
      pngAsset: 'assets/brands/artcraft/wordcraft.png',
      fallbackIcon: LucideIcons.fileType,
    ),
    'artcraft:soundcraft': BrandVisualSpec(
      key: 'artcraft:soundcraft',
      label: 'SoundCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFF10B981),
      pngAsset: 'assets/brands/artcraft/soundcraft.png',
      fallbackIcon: LucideIcons.audioWaveform,
    ),
    'artcraft:deckcraft': BrandVisualSpec(
      key: 'artcraft:deckcraft',
      label: 'DeckCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFFF59E0B),
      pngAsset: 'assets/brands/artcraft/deckcraft.png',
      fallbackIcon: LucideIcons.presentation,
    ),
    'artcraft:cadcraft': BrandVisualSpec(
      key: 'artcraft:cadcraft',
      label: 'CADCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFF06B6D4),
      pngAsset: 'assets/brands/artcraft/cadcraft.png',
      fallbackIcon: LucideIcons.compass,
    ),
    'artcraft:gridcraft': BrandVisualSpec(
      key: 'artcraft:gridcraft',
      label: 'GridCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFF16A34A),
      pngAsset: 'assets/brands/artcraft/gridcraft.png',
      fallbackIcon: LucideIcons.sheet,
    ),
    'artcraft:figcraft': BrandVisualSpec(
      key: 'artcraft:figcraft',
      label: 'FigCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFFD946EF),
      pngAsset: 'assets/brands/artcraft/figcraft.png',
      fallbackIcon: LucideIcons.component,
    ),
    'artcraft:texturecraft': BrandVisualSpec(
      key: 'artcraft:texturecraft',
      label: 'TextureCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFFEAB308),
      pngAsset: 'assets/brands/artcraft/texturecraft.png',
      fallbackIcon: LucideIcons.blend,
    ),
    'artcraft:materialcraft': BrandVisualSpec(
      key: 'artcraft:materialcraft',
      label: 'MaterialCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFF6366F1),
      pngAsset: 'assets/brands/artcraft/materialcraft.png',
      fallbackIcon: LucideIcons.globe,
    ),
    'artcraft:solidcraft': BrandVisualSpec(
      key: 'artcraft:solidcraft',
      label: 'SolidCraft',
      category: 'ArtCraft Studio',
      accent: Color(0xFF14B8A6),
      pngAsset: 'assets/brands/artcraft/solidcraft.png',
      fallbackIcon: LucideIcons.box,
    ),
  };

  static const Map<String, String> _aliases = {
    // AI Providers
    'anthropic': 'provider:anthropic',
    'claude': 'provider:anthropic',
    'openai': 'provider:openai',
    'chatgpt': 'provider:openai',
    'google': 'provider:google',
    'gemini': 'provider:google',
    'ollama': 'provider:ollama',
    'qwen': 'provider:qwen',
    'xai': 'provider:xai',
    'grok': 'provider:xai',
    'openrouter': 'provider:openrouter',
    'foundry': 'provider:foundry',
    'azure-ai': 'provider:foundry',

    // Brands
    'aspire': 'brand:aspire',
    'iaspire': 'brand:aspire',
    'diagnostics.aspire': 'brand:aspire',
    '.net aspire': 'brand:aspire',
    'dotnet-aspire': 'brand:aspire',
    'github': 'brand:github',
    'igithub': 'brand:github',
    'iworktree': 'brand:github',
    'worktree': 'brand:github',
    'gmail': 'brand:gmail',
    'igmail': 'brand:gmail',
    'google-mail': 'brand:gmail',
    'salesforce': 'brand:salesforce',
    'isalesforce': 'brand:salesforce',
    'intochat': 'brand:intochat',
    'iinostudio': 'brand:intochat',
    'ino.studio': 'brand:intochat',
    'inostudio': 'brand:intochat',
    'ino studio': 'brand:intochat',
    'ino': 'brand:intochat',
    'docker': 'brand:docker',
    'idocker': 'brand:docker',
    'dotnet': 'brand:dotnet',
    '.net': 'brand:dotnet',
    'bitcoin': 'brand:bitcoin',
    'btc': 'brand:bitcoin',

    // Capabilities
    'imemory': 'capability:memory',
    'iknowledgestore': 'capability:memory',
    'ai.memory': 'capability:memory',
    'memory': 'capability:memory',
    'icodegraph': 'capability:codegraph',
    'roslyn.codegraph': 'capability:codegraph',
    'codegraph': 'capability:codegraph',
    'code graph': 'capability:codegraph',
    'itracereader': 'capability:traces',
    'diagnostics.traces': 'capability:traces',
    'system traces': 'capability:traces',
    'traces': 'capability:traces',
    'trace': 'capability:traces',
    'ihttpread': 'capability:http',
    'http@1': 'capability:http',
    'http': 'capability:http',
    'https': 'capability:http',
    'web https': 'capability:http',
    'inotifications': 'capability:notifications',
    'notifications': 'capability:notifications',
    'iconfirmations': 'capability:shield',
    'confirmations': 'capability:shield',
    'human approval': 'capability:shield',
    'shield': 'capability:shield',
    'approval': 'capability:shield',
    'ifiles': 'capability:files',
    'ifilesystem': 'capability:files',
    'files': 'capability:files',
    'isynapsestudio': 'capability:studio',
    'synapse': 'capability:studio',
    'creator': 'app:creator',
    'studio': 'capability:studio',
    'settings': 'app:settings',
    'gallery': 'app:gallery',
    'ui-gallery': 'app:gallery',
    'desktop': 'app:desktop',
    'workspace': 'app:workspace',
    'tasks': 'app:tasks',
    'calculator': 'app:calculator',
    'calc': 'app:calculator',
    'data': 'app:data',
    'datavisualizer': 'app:data',
    'usage': 'app:usage',
    'friends': 'app:friends',
    'circles': 'app:circles',
    'post': 'app:post',
    'markdraw': 'app:markdraw',
    'brain': 'app:brain',

    // ArtCraft Studios
    'artcraft': 'artcraft:artcraft',
    'iartcraft': 'artcraft:artcraft',
    'photocraft': 'artcraft:photocraft',
    'iphotocraft': 'artcraft:photocraft',
    'filmcraft': 'artcraft:filmcraft',
    'ifilmcraft': 'artcraft:filmcraft',
    'vectorcraft': 'artcraft:vectorcraft',
    'ivectorcraft': 'artcraft:vectorcraft',
    'lightcraft': 'artcraft:lightcraft',
    'ilightcraft': 'artcraft:lightcraft',
    'effectcraft': 'artcraft:effectcraft',
    'ieffectcraft': 'artcraft:effectcraft',
    'designcraft': 'artcraft:designcraft',
    'idesigncraft': 'artcraft:designcraft',
    'pdfcraft': 'artcraft:pdfcraft',
    'ipdfcraft': 'artcraft:pdfcraft',
    'wordcraft': 'artcraft:wordcraft',
    'iwordcraft': 'artcraft:wordcraft',
    'soundcraft': 'artcraft:soundcraft',
    'isoundcraft': 'artcraft:soundcraft',
    'deckcraft': 'artcraft:deckcraft',
    'ideckcraft': 'artcraft:deckcraft',
    'cadcraft': 'artcraft:cadcraft',
    'icadcraft': 'artcraft:cadcraft',
    'gridcraft': 'artcraft:gridcraft',
    'igridcraft': 'artcraft:gridcraft',
    'figcraft': 'artcraft:figcraft',
    'ifigcraft': 'artcraft:figcraft',
    'texturecraft': 'artcraft:texturecraft',
    'itexturecraft': 'artcraft:texturecraft',
    'materialcraft': 'artcraft:materialcraft',
    'imaterialcraft': 'artcraft:materialcraft',
    'solidcraft': 'artcraft:solidcraft',
    'isolidcraft': 'artcraft:solidcraft',
  };

  /// Resolves an AI model identifier, human label, and/or provider icon key
  /// into a clean local [BrandVisualSpec].
  static BrandVisualSpec resolveModelVisual(
    String? model, {
    String? modelLabel,
    String? providerIcon,
  }) {
    final rawModel = (model ?? '').trim();
    final rawLabel = (modelLabel ?? '').trim();
    final rawProvider = (providerIcon ?? '').trim().toLowerCase();

    final provider = _resolveProviderSpec(
      rawProvider: rawProvider,
      rawModel: rawModel,
      rawLabel: rawLabel,
    );

    final formattedLabel = _resolveHumanModelLabel(
      rawModel: rawModel,
      rawLabel: rawLabel,
      fallbackProviderLabel: provider.label,
    );

    return BrandVisualSpec(
      key: provider.key,
      label: formattedLabel,
      category: provider.label,
      accent: provider.accent,
      pngAsset: provider.pngAsset,
      svgAsset: provider.svgAsset,
      fallbackIcon: provider.fallbackIcon,
      monochromeSvg: provider.monochromeSvg,
    );
  }

  /// Resolves a capability contract name, brand key, activity title, or app ID
  /// into an allowlisted [BrandVisualSpec]. Returns `null` via [tryResolveCapabilityOrAppVisual]
  /// when unrecognized, or a clean deterministic fallback from [resolveCapabilityOrAppVisual].
  static BrandVisualSpec? tryResolveCapabilityOrAppVisual(
    String? keyOrTitle, {
    String? iconKey,
  }) {
    final normalizedIcon = (iconKey ?? '').trim().toLowerCase();
    if (normalizedIcon.isNotEmpty) {
      final byIcon = _lookupCanonical(normalizedIcon);
      if (byIcon != null) return byIcon;
    }

    final raw = (keyOrTitle ?? '').trim();
    if (raw.isEmpty) return null;

    final lower = raw.toLowerCase();
    final direct = _lookupCanonical(lower);
    if (direct != null) return direct;

    // Handle compound activity titles or dotted/slashed tool identifiers:
    // e.g. 'Aspire · Resources', 'IAspire.ListResources', 'IGitHub / CreatePullRequest'
    final parts = lower
        .split(RegExp(r'[\s·•|/:.]+'))
        .map((s) => s.trim())
        .where((s) => s.isNotEmpty);
    for (final part in parts) {
      final match = _lookupCanonical(part);
      if (match != null) return match;
    }

    return null;
  }

  static BrandVisualSpec resolveCapabilityOrAppVisual(
    String? keyOrTitle, {
    String? iconKey,
  }) {
    final matched = tryResolveCapabilityOrAppVisual(
      keyOrTitle,
      iconKey: iconKey,
    );
    if (matched != null) return matched;

    final cleaned = _humanizeIdentifier(keyOrTitle ?? '');
    return BrandVisualSpec(
      key: 'capability:custom',
      label: cleaned.isEmpty ? 'Capability' : cleaned,
      category: 'Capability',
      accent: const Color(0xFF6366F1),
      fallbackIcon: LucideIcons.cpu,
    );
  }

  static BrandVisualSpec? _lookupCanonical(String lower) {
    final directCatalog = catalog[lower];
    if (directCatalog != null) return directCatalog;
    final directProvider = providers[lower];
    if (directProvider != null) return directProvider;

    final aliasTarget = _aliases[lower];
    if (aliasTarget != null) {
      return catalog[aliasTarget] ?? providers[aliasTarget];
    }

    if (lower.startsWith('provider:')) {
      final p = providers[lower];
      if (p != null) return p;
    }
    return null;
  }

  static BrandVisualSpec _resolveProviderSpec({
    required String rawProvider,
    required String rawModel,
    required String rawLabel,
  }) {
    if (rawProvider.isNotEmpty) {
      final explicit =
          providers[rawProvider] ??
          providers['provider:$rawProvider'] ??
          catalog[rawProvider];
      if (explicit != null) return explicit;
    }

    final probe = '${rawModel.toLowerCase()} ${rawLabel.toLowerCase()}'.trim();

    if (probe.contains('openrouter/') || probe.contains('openrouter')) {
      return providers['provider:openrouter']!;
    }
    if (probe.contains('claude') ||
        probe.contains('sonnet') ||
        probe.contains('opus') ||
        probe.contains('haiku') ||
        probe.contains('anthropic')) {
      return providers['provider:anthropic']!;
    }
    if (probe.contains('gpt') ||
        RegExp(r'\bo[134](?:-[a-z0-9]+)?\b').hasMatch(probe) ||
        probe.contains('openai') ||
        probe.contains('chatgpt')) {
      return providers['provider:openai']!;
    }
    if (probe.contains('gemini') ||
        probe.contains('gemma') ||
        probe.contains('google')) {
      return providers['provider:google']!;
    }
    if (probe.contains('grok') || probe.contains('xai')) {
      return providers['provider:xai']!;
    }
    if (probe.contains('foundry') ||
        probe.contains('phi-') ||
        probe.contains('phi3') ||
        probe.contains('phi4')) {
      return providers['provider:foundry']!;
    }
    if (probe.contains('qwen')) {
      // Local Ollama model tags carry `:size` (e.g. `qwen2.5-coder:14b`, `qwen3:14b`).
      if (rawModel.contains(':') || probe.contains('ollama')) {
        return providers['provider:ollama']!;
      }
      return providers['provider:qwen']!;
    }
    if (probe.contains('ollama') ||
        probe.contains('llama') ||
        probe.contains('mistral') ||
        probe.contains('deepseek') ||
        probe.contains('codellama') ||
        probe.contains('nomic') ||
        rawModel.contains(':')) {
      return providers['provider:ollama']!;
    }

    return const BrandVisualSpec(
      key: 'brand:intochat',
      label: 'Ino',
      category: 'Intelligence',
      accent: Color(0xFF6366F1),
      svgAsset: 'assets/brands/ic-mark.svg',
      fallbackIcon: LucideIcons.sparkles,
    );
  }

  static String _resolveHumanModelLabel({
    required String rawModel,
    required String rawLabel,
    required String fallbackProviderLabel,
  }) {
    if (rawLabel.isNotEmpty && !_looksLikeRawSlug(rawLabel)) {
      return rawLabel;
    }

    final target = rawModel.isNotEmpty ? rawModel : rawLabel;
    if (target.isEmpty) return fallbackProviderLabel;

    var slug = target.trim();
    if (slug.contains('/')) {
      slug = slug.split('/').last;
    }
    final lower = slug
        .toLowerCase()
        .replaceFirst(RegExp(r'-\d{8}$'), '')
        .replaceFirst(RegExp(r'@\d{8}$'), '');

    // Anthropic Claude family
    if (lower == 'claude-sonnet-4-6' ||
        lower.startsWith('claude-sonnet-4-6-') ||
        lower == 'sonnet-4.6' ||
        lower == 'sonnet-4-6' ||
        lower == 'sonnet46' ||
        lower == 'isonnet46') {
      return 'Sonnet 4.6';
    }
    if (lower == 'claude-opus-4-6' ||
        lower.startsWith('claude-opus-4-6-') ||
        lower == 'opus-4.6' ||
        lower == 'opus-4-6' ||
        lower == 'opus46' ||
        lower == 'iopus46') {
      return 'Opus 4.6';
    }
    if (lower == 'claude-sonnet-5' ||
        lower.startsWith('claude-sonnet-5-') ||
        lower == 'sonnet-5' ||
        lower == 'sonnet5' ||
        lower == 'isonnet5') {
      return 'Sonnet 5';
    }
    if (lower == 'claude-haiku-4-5' ||
        lower.startsWith('claude-haiku-4-5-') ||
        lower == 'haiku-4.5' ||
        lower == 'haiku-4-5' ||
        lower == 'haiku45' ||
        lower == 'ihaiku45') {
      return 'Haiku 4.5';
    }
    if (lower.contains('sonnet-3-7') || lower.contains('3-7-sonnet')) {
      return 'Sonnet 3.7';
    }
    if (lower.contains('sonnet-3-5') || lower.contains('3-5-sonnet')) {
      return 'Sonnet 3.5';
    }
    final claudeMatch = RegExp(r'^claude-(sonnet|opus|haiku)-(\d+)(?:-(\d+))?$')
        .firstMatch(lower);
    if (claudeMatch != null) {
      final tier = _capitalize(claudeMatch.group(1)!);
      final major = claudeMatch.group(2)!;
      final minor = claudeMatch.group(3);
      return minor != null ? '$tier $major.$minor' : '$tier $major';
    }

    // OpenAI GPT family
    if (lower == 'gpt-5.4' ||
        lower == 'gpt-5-4' ||
        lower == 'gpt54' ||
        lower == 'igpt54') {
      return 'GPT-5.4';
    }
    if (lower == 'gpt-5.6-luna' || lower == 'igpt56luna') {
      return 'GPT-5.6 Luna';
    }
    if (lower.startsWith('gpt-')) {
      final rest = slug.substring(4);
      final parts = rest.split('-');
      if (parts.length == 1) return 'GPT-${parts.first}';
      final ver = parts.first;
      final suffix = parts.skip(1).map(_capitalize).join(' ');
      return 'GPT-$ver $suffix';
    }

    // Google Gemini family
    if (lower == 'gemini-3.6-pro' ||
        lower == 'gemini-3-6-pro' ||
        lower == 'igemini36pro') {
      return 'Gemini 3.6 Pro';
    }
    if (lower == 'gemini-3.6-flash' ||
        lower == 'gemini-3-6-flash' ||
        lower == 'igemini36flash') {
      return 'Gemini 3.6 Flash';
    }
    if (lower.startsWith('gemini-')) {
      final tokens = lower.substring(7).split('-');
      return 'Gemini ${tokens.map(_capitalizeModelToken).join(' ')}';
    }

    // Qwen / Ollama family
    if (lower == 'qwen2.5-coder:14b' || lower == 'qwen2.5-coder-14b') {
      return 'Qwen 2.5 Coder 14B';
    }
    if (lower == 'qwen3:14b' || lower == 'qwen3-14b') {
      return 'Qwen 3 14B';
    }
    final qwenMatch = RegExp(
      r'^qwen(\d+(?:\.\d+)?)(?:-([a-z0-9-]+))?(?::([a-z0-9.]+))?$',
    ).firstMatch(lower);
    if (qwenMatch != null) {
      final ver = qwenMatch.group(1)!;
      final variant = qwenMatch.group(2);
      final size = qwenMatch.group(3);
      final buf = <String>['Qwen', ver];
      if (variant != null && variant.isNotEmpty) {
        buf.addAll(variant.split('-').map(_capitalizeModelToken));
      }
      if (size != null && size.isNotEmpty) {
        buf.add(size.toUpperCase());
      }
      return buf.join(' ');
    }

    if (rawLabel.isNotEmpty) return rawLabel;
    return _humanizeModelFallback(slug);
  }

  static bool _looksLikeRawSlug(String text) {
    if (text.contains(' ')) return false;
    final lower = text.toLowerCase();
    return lower.contains('-') ||
        lower.contains(':') ||
        lower.contains('/') ||
        (text.length > 2 &&
            text.startsWith('I') &&
            text[1].toUpperCase() == text[1] &&
            text[1].toLowerCase() != text[1]);
  }

  static String _humanizeIdentifier(String raw) {
    var trimmed = raw.trim();
    if (trimmed.isEmpty) return '';
    if (trimmed.contains(':')) {
      trimmed = trimmed.split(':').last;
    }
    // Strip C# interface prefix like `IAspire` -> `Aspire`
    if (trimmed.length > 2 &&
        trimmed.startsWith('I') &&
        trimmed[1].toUpperCase() == trimmed[1] &&
        trimmed[1].toLowerCase() != trimmed[1]) {
      trimmed = trimmed.substring(1);
    }
    final spaced = trimmed
        .replaceAllMapped(
          RegExp(r'([a-z0-9])([A-Z])'),
          (m) => '${m.group(1)} ${m.group(2)}',
        )
        .replaceAll(RegExp(r'[._-]+'), ' ')
        .trim();
    return spaced
        .split(RegExp(r'\s+'))
        .where((s) => s.isNotEmpty)
        .map(_capitalize)
        .join(' ');
  }

  static String _humanizeModelFallback(String slug) {
    if (slug.contains(':')) {
      final idx = slug.indexOf(':');
      final base = slug.substring(0, idx);
      final tag = slug.substring(idx + 1).toUpperCase();
      final baseClean = base
          .split(RegExp(r'[-_]+'))
          .where((s) => s.isNotEmpty)
          .map(_capitalizeModelToken)
          .join(' ');
      return tag.isEmpty ? baseClean : '$baseClean $tag';
    }
    return slug
        .split(RegExp(r'[-_]+'))
        .where((s) => s.isNotEmpty)
        .map(_capitalizeModelToken)
        .join(' ');
  }

  static String _capitalizeModelToken(String token) {
    if (RegExp(r'^\d+(\.\d+)?[bmk]$', caseSensitive: false).hasMatch(token)) {
      return token.toUpperCase();
    }
    if (token.toLowerCase() == 'gpt') return 'GPT';
    if (token.toLowerCase() == 'vl') return 'VL';
    return _capitalize(token);
  }

  static String _capitalize(String s) {
    if (s.isEmpty) return s;
    return '${s[0].toUpperCase()}${s.substring(1)}';
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
    return Icon(spec.fallbackIcon, size: size, color: spec.accent);
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

    return Container(
      padding: EdgeInsets.symmetric(
        horizontal: compact ? 6 : 8,
        vertical: compact ? 2 : 3,
      ),
      decoration: BoxDecoration(
        color: Color.alphaBlend(
          spec.accent.withValues(alpha: c.isDark ? 0.12 : 0.08),
          c.surfaceSunk,
        ),
        borderRadius: BorderRadius.circular(999),
        border: Border.all(
          color: spec.accent.withValues(alpha: c.isDark ? 0.28 : 0.22),
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
