import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';

import 'brand_visuals.dart';
import 'ui_colors.dart';

enum NeuronIconKind {
  assistant,
  conversation,
  execution,
  gmail,
  salesforce,
  aspire,
  muncly,
  github,
  intochat,
  docker,
  dotnet,
  bitcoin,
  anthropic,
  openai,
  google,
  ollama,
  qwen,
  xai,
  openrouter,
  foundry,
  artcraft,
  photocraft,
  filmcraft,
  vectorcraft,
  lightcraft,
  effectcraft,
  designcraft,
  pdfcraft,
  wordcraft,
  soundcraft,
  deckcraft,
  cadcraft,
  gridcraft,
  figcraft,
  texturecraft,
  materialcraft,
  solidcraft,
  traces,
  http,
  notifications,
  shield,
  files,
  studio,
  review,
  architecture,
  quality,
  document,
  search,
  repository,
  memory,
  clock,
  generic;

  /// Only known presentation keys may select a bundled icon. Never interpret a
  /// server value as an asset path, URL, or a substring of a provider name.
  static NeuronIconKind fromKey(String? key) {
    if (key == null || key.isEmpty) return generic;
    final lower = key.trim().toLowerCase();
    for (final kind in values) {
      if (kind.name == lower) return kind;
    }
    final mapped = _namespacedKeys[lower];
    if (mapped != null) return mapped;
    return generic;
  }

  static const Map<String, NeuronIconKind> _namespacedKeys = {
    'brand:aspire': aspire,
    'brand:muncly': muncly,
    'brand:github': github,
    'brand:gmail': gmail,
    'brand:salesforce': salesforce,
    'brand:intochat': intochat,
    'brand:docker': docker,
    'brand:dotnet': dotnet,
    'brand:bitcoin': bitcoin,
    'provider:anthropic': anthropic,
    'provider:openai': openai,
    'provider:google': google,
    'provider:ollama': ollama,
    'provider:qwen': qwen,
    'provider:xai': xai,
    'provider:openrouter': openrouter,
    'provider:foundry': foundry,
    'provider:foundrylocal': foundry,
    'provider:openaicompatible': openai,
    'provider:sherpaonnx': openai,
    'capability:memory': memory,
    'capability:codegraph': architecture,
    'capability:traces': traces,
    'capability:http': http,
    'capability:notifications': notifications,
    'capability:shield': shield,
    'capability:files': files,
    'capability:studio': studio,
    'artcraft:artcraft': artcraft,
    'artcraft:photocraft': photocraft,
    'artcraft:filmcraft': filmcraft,
    'artcraft:vectorcraft': vectorcraft,
    'artcraft:lightcraft': lightcraft,
    'artcraft:effectcraft': effectcraft,
    'artcraft:designcraft': designcraft,
    'artcraft:pdfcraft': pdfcraft,
    'artcraft:wordcraft': wordcraft,
    'artcraft:soundcraft': soundcraft,
    'artcraft:deckcraft': deckcraft,
    'artcraft:cadcraft': cadcraft,
    'artcraft:gridcraft': gridcraft,
    'artcraft:figcraft': figcraft,
    'artcraft:texturecraft': texturecraft,
    'artcraft:materialcraft': materialcraft,
    'artcraft:solidcraft': solidcraft,
  };
}

/// One allowlisted local icon vocabulary for graph tiles and their inspectors.
final class NeuronIcon extends StatelessWidget {
  const NeuronIcon({
    super.key,
    required this.kind,
    this.size = 30,
    this.color,
    this.semanticLabel,
  });

  factory NeuronIcon.fromBrandKey(
    String? key, {
    Key? widgetKey,
    double size = 30,
    Color? color,
    String? semanticLabel,
  }) {
    return NeuronIcon(
      key: widgetKey,
      kind: NeuronIconKind.fromKey(key),
      size: size,
      color: color,
      semanticLabel: semanticLabel,
    );
  }

  final NeuronIconKind kind;
  final double size;
  final Color? color;
  final String? semanticLabel;

  @override
  Widget build(BuildContext context) {
    final pngAsset = BrandVisuals.pngAssetForId(kind.name);
    if (pngAsset != null) {
      return Image.asset(
        pngAsset,
        width: size,
        height: size,
        fit: BoxFit.contain,
        filterQuality: FilterQuality.medium,
        semanticLabel: semanticLabel ?? kind.name,
        errorBuilder: (_, _, _) => _buildSvgOrGlyph(context),
      );
    }
    return _buildSvgOrGlyph(context);
  }

  Widget _buildSvgOrGlyph(BuildContext context) {
    final svgAsset = BrandVisuals.svgAssetForId(kind.name);
    if (svgAsset != null) {
      return SvgPicture.asset(
        svgAsset,
        width: size,
        height: size,
        semanticsLabel: semanticLabel ?? kind.name,
      );
    }
    final spec = BrandVisuals.tryForIconKey('capability:${kind.name}');
    if (spec != null && spec.hasAsset) {
      return IcBrandIcon(spec: spec, size: size);
    }
    return Icon(
      glyphOf(kind),
      size: size,
      color: color ?? UiColors.of(context).accent,
      semanticLabel: semanticLabel,
    );
  }

  /// The material glyph of a kind; brand kinds, drawn from PNG/SVG, answer a clean glyph fallback.
  static IconData glyphOf(NeuronIconKind kind) => switch (kind) {
    NeuronIconKind.assistant => Icons.auto_awesome_rounded,
    NeuronIconKind.conversation => Icons.chat_bubble_outline_rounded,
    NeuronIconKind.execution => Icons.bolt_rounded,
    NeuronIconKind.document => Icons.description_outlined,
    NeuronIconKind.search => Icons.search_rounded,
    NeuronIconKind.repository => Icons.code_rounded,
    NeuronIconKind.review => Icons.fact_check_outlined,
    NeuronIconKind.architecture => Icons.account_tree_outlined,
    NeuronIconKind.quality => Icons.rule_folder_outlined,
    NeuronIconKind.memory => Icons.inventory_2_outlined,
    NeuronIconKind.clock => Icons.schedule_rounded,
    NeuronIconKind.traces => Icons.monitor_heart_outlined,
    NeuronIconKind.http => Icons.language_rounded,
    NeuronIconKind.notifications => Icons.notifications_none_rounded,
    NeuronIconKind.shield => Icons.verified_user_outlined,
    NeuronIconKind.files => Icons.folder_outlined,
    NeuronIconKind.studio => Icons.auto_fix_high_rounded,
    _ => Icons.hub_outlined,
  };
}
