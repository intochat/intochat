import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../components/image/ui_image.dart';
import '../components/kit/ui_kit_surface.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import '../theme/wire_node_values.dart';
import 'node_common.dart';
import 'node_context.dart';
import 'node_renderer.dart';

Uint8List? _tryDecodeDataUrl(String raw) {
  final trimmed = raw.trim();
  if (!trimmed.startsWith('data:image/')) return null;
  final comma = trimmed.indexOf(',');
  if (comma <= 0 || comma + 1 >= trimmed.length) return null;
  try {
    return base64Decode(trimmed.substring(comma + 1));
  } catch (_) {
    return null;
  }
}

Widget renderSurface(SurfaceNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final theme = Theme.of(context);
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final variant = parseSurfaceVariant(node.surfaceKind);

    // 1. CommandBar / Toolbar surface (`kind: "toolbar"`)
    if (variant.kind == SurfaceVariantKind.toolbar) {
      final items = renderAll(node.children, ctx);
      return Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: c.surfaceSunk,
          borderRadius: BorderRadius.circular(tokens.surfaceRadius),
          border: Border.all(color: c.borderHairline),
        ),
        child: Wrap(
          spacing: 6,
          runSpacing: 4,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: items,
        ),
      );
    }

    // 2. Breadcrumb bar (`kind: "breadcrumb"`)
    if (variant.kind == SurfaceVariantKind.breadcrumb) {
      final items = renderAll(node.children, ctx);
      return SingleChildScrollView(
        primary: false,
        scrollDirection: Axis.horizontal,
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            for (var i = 0; i < items.length; i++) ...[
              if (i > 0)
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 4),
                  child: Icon(
                    LucideIcons.chevronRight,
                    size: 14,
                    color: c.subtle,
                  ),
                ),
              items[i],
            ],
          ],
        ),
      );
    }

    // 3. Inline raster / SVG / network Image canvas (`kind: "image"` | `"image:contain"` | `"image:cover"`)
    if (variant.kind == SurfaceVariantKind.image) {
      final boxFit = variant.boxFit;
      final src = (node.subtitle ?? '').trim();
      final caption = (node.title ?? '').trim();
      final height = node.padding > 0
          ? node.padding.clamp(80.0, 1200.0)
          : 320.0;
      final bytes = _tryDecodeDataUrl(src);

      Widget content;
      if (bytes != null) {
        content = ClipRRect(
          borderRadius: BorderRadius.circular(8),
          child: SizedBox(
            height: height,
            width: double.infinity,
            child: CustomPaint(
              painter: _StudioCheckerboardPainter(
                cellA: c.isDark
                    ? const Color(0xFF181A20)
                    : const Color(0xFFE8E6E1),
                cellB: c.isDark
                    ? const Color(0xFF22252E)
                    : const Color(0xFFF3F1EC),
              ),
              child: Image.memory(
                bytes,
                fit: boxFit,
                filterQuality: FilterQuality.medium,
              ),
            ),
          ),
        );
      } else if (src.isNotEmpty) {
        content = SizedBox(
          height: height,
          width: double.infinity,
          child: UiImage.network(url: src, caption: caption),
        );
      } else {
        content = SizedBox(
          height: height,
          child: Center(
            child: Text(
              'No image preview available',
              style: UiType.of(context).bodyMuted,
            ),
          ),
        );
      }

      return Container(
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          color: c.surfaceSunk,
          borderRadius: BorderRadius.circular(tokens.surfaceRadius),
          border: Border.all(color: c.borderHairline),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: 8,
          children: [
            if (caption.isNotEmpty)
              Row(
                children: [
                  Icon(LucideIcons.image, size: 14, color: c.muted),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      caption,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: UiType.textStyle(
                        12.5,
                        weight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                  ),
                ],
              ),
            content,
          ],
        ),
      );
    }

    // 4. IDE Code viewer (`kind: "code"` | `"code:<lang>"`)
    if (variant.kind == SurfaceVariantKind.code) {
      final lang = variant.language;
      final title = (node.title ?? '').trim();
      final source = node.subtitle ?? '';
      final maxHeight = node.padding > 0
          ? node.padding.clamp(120.0, 1200.0)
          : 360.0;
      return _CodeSurfaceView(
        title: title.isEmpty ? 'Source' : title,
        language: lang,
        source: source,
        maxHeight: maxHeight,
      );
    }

    // 5. Interactive collapsible Expander (`kind: "expander"` | `"expander:open"` | `"expander:closed"`)
    if (variant.kind == SurfaceVariantKind.expander) {
      return _ExpanderSurfaceView(
        title: node.title ?? 'Details',
        subtitle: node.subtitle,
        initiallyExpanded: variant.initiallyOpen,
        children: renderAll(node.children, ctx),
      );
    }

    // 6. Bounded vertical ScrollBox (`kind: "scroll"`)
    if (variant.kind == SurfaceVariantKind.scroll) {
      final maxHeight = node.padding > 0
          ? node.padding.clamp(100.0, 1400.0)
          : 360.0;
      return ConstrainedBox(
        constraints: BoxConstraints(maxHeight: maxHeight),
        child: SingleChildScrollView(
          primary: false,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            spacing: 10,
            children: renderAll(node.children, ctx),
          ),
        ),
      );
    }

    // 7. Live App Studio device/viewport frame (`kind: "studio-frame"` | `"studio-frame:<width>"`)
    if (variant.kind == SurfaceVariantKind.studioFrame) {
      final targetWidth = variant.targetWidth;
      final singleFill =
          node.children.length == 1 && NodeRenderer.fills(node.children.single);
      final renderedChildren = renderAll(node.children, ctx);
      final frameBody = LayoutBuilder(
        builder: (context, constraints) {
          final stretch =
              singleFill &&
              constraints.hasBoundedHeight &&
              constraints.maxHeight.isFinite &&
              constraints.maxHeight >= 180;
          return Container(
            padding: EdgeInsets.all(node.padding.clamp(8.0, 32.0)),
            decoration: BoxDecoration(
              color: c.page,
              borderRadius: BorderRadius.circular(tokens.surfaceRadius),
              border: Border.all(color: c.borderStrong),
              boxShadow: UiShadows.md(c),
            ),
            child: Column(
              mainAxisSize: stretch ? MainAxisSize.max : MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: 12,
              children: [
                if (node.title case final title? when title.isNotEmpty)
                  Container(
                    padding: const EdgeInsets.only(bottom: 8),
                    decoration: BoxDecoration(
                      border: Border(
                        bottom: BorderSide(color: c.borderHairline),
                      ),
                    ),
                    child: Row(
                      children: [
                        Icon(LucideIcons.monitor, size: 14, color: c.accent),
                        const SizedBox(width: 6),
                        Expanded(
                          child: Text(
                            title,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: UiType.textStyle(
                              12.5,
                              weight: FontWeight.w600,
                              color: c.textStrong,
                            ),
                          ),
                        ),
                        if (node.subtitle case final sub? when sub.isNotEmpty)
                          Text(
                            sub,
                            style: UiType.textStyle(
                              11.5,
                              weight: FontWeight.w500,
                              color: c.muted,
                            ),
                          ),
                      ],
                    ),
                  ),
                if (stretch)
                  Expanded(child: renderedChildren.single)
                else
                  ...renderedChildren,
              ],
            ),
          );
        },
      );

      if (targetWidth != null && targetWidth > 0) {
        return Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: c.surfaceSunk,
            borderRadius: BorderRadius.circular(tokens.surfaceRadius),
            border: Border.all(color: c.borderHairline),
          ),
          child: Center(
            child: ConstrainedBox(
              constraints: BoxConstraints(maxWidth: targetWidth),
              child: frameBody,
            ),
          ),
        );
      }
      return frameBody;
    }

    // Standard material surfaces (`recessed`, `elevated`, `acrylic`, `subtle`)
    final (color, borderColor, shadows, blurSigma) = switch (variant.kind) {
      SurfaceVariantKind.recessed => (
        c.recessed,
        c.hairline,
        const <BoxShadow>[],
        0.0,
      ),
      SurfaceVariantKind.elevated => (
        c.elevated,
        c.isGlass ? c.specularBorder : c.hairline,
        UiShadows.md(c),
        c.blurSigma,
      ),
      SurfaceVariantKind.acrylic => (
        c.chrome,
        c.chromeBorder,
        UiShadows.sm(c),
        c.blurSigma > 0 ? c.blurSigma : 20.0,
      ),
      _ => (
        c.surface,
        c.isGlass ? c.specularBorder : c.hairline,
        UiShadows.sm(c),
        0.0,
      ),
    };
    final singleFill =
        node.children.length == 1 && NodeRenderer.fills(node.children.single);
    final renderedChildren = renderAll(node.children, ctx);
    return LayoutBuilder(
      builder: (context, constraints) {
        final stretch =
            singleFill &&
            constraints.hasBoundedHeight &&
            constraints.maxHeight.isFinite &&
            constraints.maxHeight >= 180;
        return UiKitSurface(
          padding: EdgeInsets.all(node.padding.clamp(0, 128)),
          color: color,
          borderColor: borderColor,
          shadows: shadows,
          blurSigma: blurSigma,
          child: Column(
            mainAxisSize: stretch ? MainAxisSize.max : MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            spacing: 12,
            children: [
              if (node.title case final title? when title.isNotEmpty)
                Text(title, style: theme.textTheme.titleMedium),
              if (node.subtitle case final subtitle? when subtitle.isNotEmpty)
                Text(subtitle, style: theme.textTheme.bodySmall),
              if (stretch)
                Expanded(child: renderedChildren.single)
              else
                ...renderedChildren,
            ],
          ),
        );
      },
    );
  },
);

class _CodeSurfaceView extends StatefulWidget {
  const _CodeSurfaceView({
    required this.title,
    required this.language,
    required this.source,
    required this.maxHeight,
  });

  final String title;
  final String language;
  final String source;
  final double maxHeight;

  @override
  State<_CodeSurfaceView> createState() => _CodeSurfaceViewState();
}

class _CodeSurfaceViewState extends State<_CodeSurfaceView> {
  bool _copied = false;

  Future<void> _copy() async {
    await Clipboard.setData(ClipboardData(text: widget.source));
    if (!mounted) return;
    setState(() => _copied = true);
    await Future<void>.delayed(const Duration(seconds: 2));
    if (mounted) setState(() => _copied = false);
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final lines = splitSourceLines(widget.source);

    return Container(
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        borderRadius: BorderRadius.circular(tokens.surfaceRadius),
        border: Border.all(color: c.borderHairline),
      ),
      clipBehavior: Clip.antiAlias,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            decoration: BoxDecoration(
              color: c.surfaceElevated,
              border: Border(bottom: BorderSide(color: c.borderHairline)),
            ),
            child: Row(
              children: [
                Icon(LucideIcons.fileCode, size: 14, color: c.accent),
                const SizedBox(width: 6),
                Expanded(
                  child: Text(
                    widget.title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: UiType.textStyle(
                      12.5,
                      weight: FontWeight.w600,
                      color: c.textStrong,
                    ),
                  ),
                ),
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 6,
                    vertical: 2,
                  ),
                  decoration: BoxDecoration(
                    color: c.surfaceSunk,
                    borderRadius: BorderRadius.circular(4),
                    border: Border.all(color: c.borderHairline),
                  ),
                  child: Text(
                    widget.language.toUpperCase(),
                    style: UiType.textStyle(
                      10,
                      weight: FontWeight.w600,
                      color: c.muted,
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                InkWell(
                  onTap: _copy,
                  borderRadius: BorderRadius.circular(6),
                  child: Padding(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 6,
                      vertical: 4,
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(
                          _copied ? LucideIcons.check : LucideIcons.copy,
                          size: 13,
                          color: _copied ? c.done : c.muted,
                        ),
                        const SizedBox(width: 4),
                        Text(
                          _copied ? 'Copied' : 'Copy',
                          style: UiType.textStyle(
                            11.5,
                            weight: FontWeight.w600,
                            color: _copied ? c.done : c.muted,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ],
            ),
          ),
          ConstrainedBox(
            constraints: BoxConstraints(maxHeight: widget.maxHeight),
            child: SingleChildScrollView(
              primary: false,
              padding: const EdgeInsets.all(12),
              child: SingleChildScrollView(
                primary: false,
                scrollDirection: Axis.horizontal,
                child: SelectableText(
                  [
                    for (var i = 0; i < lines.length; i++)
                      '${'${i + 1}'.padLeft(3)}  ${lines[i]}',
                  ].join('\n'),
                  style: TextStyle(
                    fontFamily: 'JetBrains Mono',
                    fontFamilyFallback: const ['monospace'],
                    fontSize: 12,
                    height: 1.45,
                    color: c.textBody,
                  ),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _ExpanderSurfaceView extends StatefulWidget {
  const _ExpanderSurfaceView({
    required this.title,
    required this.subtitle,
    required this.initiallyExpanded,
    required this.children,
  });

  final String title;
  final String? subtitle;
  final bool initiallyExpanded;
  final List<Widget> children;

  @override
  State<_ExpanderSurfaceView> createState() => _ExpanderSurfaceViewState();
}

class _ExpanderSurfaceViewState extends State<_ExpanderSurfaceView> {
  late bool _expanded = widget.initiallyExpanded;

  @override
  void didUpdateWidget(covariant _ExpanderSurfaceView oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.initiallyExpanded != widget.initiallyExpanded) {
      _expanded = widget.initiallyExpanded;
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);

    return Container(
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(tokens.surfaceRadius),
        border: Border.all(color: c.borderHairline),
      ),
      clipBehavior: Clip.antiAlias,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          InkWell(
            onTap: () => setState(() => _expanded = !_expanded),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
              child: Row(
                children: [
                  Icon(
                    _expanded
                        ? LucideIcons.chevronDown
                        : LucideIcons.chevronRight,
                    size: 16,
                    color: c.muted,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(
                          widget.title,
                          style: UiType.textStyle(
                            13,
                            weight: FontWeight.w600,
                            color: c.textStrong,
                          ),
                        ),
                        if (widget.subtitle case final sub? when sub.isNotEmpty)
                          Text(
                            sub,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: UiType.textStyle(
                              11.5,
                              weight: FontWeight.w400,
                              color: c.muted,
                            ),
                          ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
          if (_expanded && widget.children.isNotEmpty)
            Container(
              padding: const EdgeInsets.fromLTRB(14, 10, 14, 14),
              decoration: BoxDecoration(
                border: Border(top: BorderSide(color: c.borderHairline)),
              ),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                spacing: 10,
                children: widget.children,
              ),
            ),
        ],
      ),
    );
  }
}

class _StudioCheckerboardPainter extends CustomPainter {
  const _StudioCheckerboardPainter({required this.cellA, required this.cellB});

  final Color cellA;
  final Color cellB;

  @override
  void paint(Canvas canvas, Size size) {
    const step = 16.0;
    final paintA = Paint()..color = cellA;
    final paintB = Paint()..color = cellB;
    canvas.drawRect(Offset.zero & size, paintA);
    for (var y = 0.0; y < size.height; y += step) {
      final row = (y / step).floor();
      for (var x = (row.isEven ? step : 0.0); x < size.width; x += step * 2) {
        final w = (x + step > size.width) ? (size.width - x) : step;
        final h = (y + step > size.height) ? (size.height - y) : step;
        canvas.drawRect(Rect.fromLTWH(x, y, w, h), paintB);
      }
    }
  }

  @override
  bool shouldRepaint(covariant _StudioCheckerboardPainter oldDelegate) =>
      oldDelegate.cellA != cellA || oldDelegate.cellB != cellB;
}
