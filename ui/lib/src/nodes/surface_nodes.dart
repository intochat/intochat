import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/app_tile.dart';
import '../primitives/layout_primitives.dart';
import '../primitives/style.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import '../theme/wire_node_values.dart';
import 'action_nodes.dart';
import 'node_common.dart';
import 'node_context.dart';
import 'node_renderer.dart';

// --- src/nodes/card.dart ---
Widget renderCard(CardNode node, NodeContext ctx) => LayoutBuilder(
  builder: (context, constraints) {
    final text = Theme.of(context).textTheme;
    final singleFill =
        node.children.length == 1 && NodeRenderer.fills(node.children.single);
    final stretch =
        singleFill &&
        constraints.hasBoundedHeight &&
        constraints.maxHeight.isFinite &&
        constraints.maxHeight >= 180;
    final renderedChildren = renderAll(node.children, ctx);
    final structuredStyle = node.style is Map
        ? UiStyleData.fromWire(node.style)
        : null;
    return UiMixSurface(
      style: structuredStyle,
      child: Column(
        mainAxisSize: stretch ? MainAxisSize.max : MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 8,
        children: [
          ...renderAll([?node.header], ctx),
          if (node.title.isNotEmpty) Text(node.title, style: text.titleMedium),
          if (node.body.isNotEmpty) Text(node.body, style: text.bodyMedium),
          if (stretch)
            Expanded(child: renderedChildren.single)
          else
            ...renderedChildren,
          ...renderAll([?node.footer], ctx),
        ],
      ),
    );
  },
);

// --- src/nodes/surface.dart ---
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

    // 3. IDE Code viewer (`kind: "code"` | `"code:<lang>"`)
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

    // 4. Bounded vertical ScrollBox (`kind: "scroll"`)
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

    // 6. Live App Studio device/viewport frame (`kind: "studio-frame"` | `"studio-frame:<width>"`)
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
    final structuredStyle = node.style is Map
        ? UiStyleData.fromWire(node.style)
        : null;
    final (
      baseColor,
      borderColor,
      baseShadows,
      blurSigma,
    ) = switch (variant.kind) {
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
    final color = structuredStyle != null && structuredStyle.hasCustomTone
        ? UiStyleMapper.resolveToneBackground(c, structuredStyle.tone)
        : baseColor;
    final shadows =
        structuredStyle != null && structuredStyle.hasCustomElevation
        ? UiStyleMapper.elevationShadows(c, structuredStyle.elevation)
        : baseShadows;
    final fallbackPad = node.padding.clamp(0.0, 128.0);
    final effectivePadding =
        structuredStyle != null &&
            (structuredStyle.paddingX != null ||
                structuredStyle.paddingY != null)
        ? EdgeInsets.symmetric(
            horizontal: (structuredStyle.paddingX ?? fallbackPad).clamp(
              0.0,
              128.0,
            ),
            vertical: (structuredStyle.paddingY ?? fallbackPad).clamp(
              0.0,
              128.0,
            ),
          )
        : EdgeInsets.all(fallbackPad);
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
        return UiMixSurface(
          padding: effectivePadding,
          color: color,
          borderColor: borderColor,
          shadows: shadows,
          blurSigma: blurSigma,
          style: structuredStyle,
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

// --- src/nodes/nav_item.dart ---

Widget renderNavItem(NavItemNode node, NodeContext ctx) =>
    _NavItemPill(key: ValueKey('nav:${node.key}'), node: node, ctx: ctx);

class _NavItemPill extends StatefulWidget {
  const _NavItemPill({super.key, required this.node, required this.ctx});

  final NavItemNode node;
  final NodeContext ctx;

  @override
  State<_NavItemPill> createState() => _NavItemPillState();
}

class _NavItemPillState extends State<_NavItemPill> {
  bool _hover = false;
  bool _focused = false;

  void _activate() {
    if (widget.ctx.interactive) {
      pressCommand(widget.node.key, widget.node.command, widget.ctx, context);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final disableAnimations = MediaQuery.disableAnimationsOf(context);
    final duration = disableAnimations ? Duration.zero : tokens.fast;
    final node = widget.node;
    final selected = node.selected;
    final interactive = widget.ctx.interactive;
    final bg = selected
        ? c.accentTint
        : (_hover && interactive ? c.menuHover : Colors.transparent);
    final fg = selected ? c.accent : (_hover ? c.strong : c.body);
    final iconData =
        (node.icon.isNotEmpty ? uiIcon(node.icon) : null) ??
        LucideIcons.circleDot;

    return Semantics(
      button: true,
      selected: selected,
      enabled: interactive,
      excludeSemantics: true,
      label: controlNameOf(node.automationName, node.label, node.key),
      child: FocusableActionDetector(
        enabled: interactive,
        mouseCursor: interactive ? SystemMouseCursors.click : MouseCursor.defer,
        onShowFocusHighlight: (f) => setState(() => _focused = f),
        onShowHoverHighlight: (h) => setState(() => _hover = h),
        actions: {
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) {
              _activate();
              return null;
            },
          ),
        },
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: interactive ? _activate : null,
          child: Stack(
            clipBehavior: Clip.none,
            children: [
              if (_focused)
                Positioned(
                  left: -2,
                  top: -2,
                  right: -2,
                  bottom: -2,
                  child: IgnorePointer(
                    child: DecoratedBox(
                      key: ValueKey('nav_ring:${node.key}'),
                      decoration: BoxDecoration(
                        borderRadius: BorderRadius.circular(tokens.sm + 2),
                        border: Border.all(color: c.focus, width: 3),
                      ),
                    ),
                  ),
                ),
              AnimatedContainer(
                duration: duration,
                curve: tokens.ease,
                height: tokens.rowHeight,
                decoration: BoxDecoration(
                  color: bg,
                  borderRadius: BorderRadius.circular(tokens.sm),
                  border: _focused ? Border.all(color: c.accent) : null,
                ),
                child: Row(
                  children: [
                    AnimatedContainer(
                      duration: duration,
                      curve: tokens.ease,
                      width: 3,
                      height: selected ? 18 : 0,
                      decoration: BoxDecoration(
                        color: selected ? c.accent : Colors.transparent,
                        borderRadius: BorderRadius.circular(tokens.pill),
                      ),
                    ),
                    const SizedBox(width: 7),
                    Icon(
                      iconData,
                      size: 16,
                      color: selected ? c.accent : c.muted,
                    ),
                    SizedBox(width: tokens.p6),
                    Expanded(
                      child: Text(
                        node.label,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: UiType.textStyle(
                          13,
                          weight: selected ? FontWeight.w600 : FontWeight.w500,
                          color: fg,
                        ),
                      ),
                    ),
                    if (node.badge case final badge? when badge.isNotEmpty) ...[
                      const SizedBox(width: 6),
                      Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 6,
                          vertical: 2,
                        ),
                        decoration: BoxDecoration(
                          color: selected
                              ? c.accent.withValues(alpha: 0.14)
                              : c.recessed,
                          borderRadius: BorderRadius.circular(tokens.xs),
                        ),
                        child: Text(
                          badge,
                          style: UiType.tabularStyle(
                            11,
                            weight: FontWeight.w600,
                            color: selected ? c.accent : c.muted,
                          ),
                        ),
                      ),
                    ],
                    const SizedBox(width: 10),
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

// --- src/nodes/app_tile.dart ---
Widget renderAppTile(AppTileNode node, NodeContext ctx) =>
    _AppTileCard(key: ValueKey('appTile:${node.key}'), node: node, ctx: ctx);

class _AppTileCard extends StatefulWidget {
  const _AppTileCard({super.key, required this.node, required this.ctx});

  final AppTileNode node;
  final NodeContext ctx;

  @override
  State<_AppTileCard> createState() => _AppTileCardState();
}

class _AppTileCardState extends State<_AppTileCard> {
  bool _hover = false;
  bool _focused = false;

  void _activate() {
    if (widget.ctx.interactive) {
      pressCommand(widget.node.key, widget.node.command, widget.ctx, context);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final disableAnimations = MediaQuery.disableAnimationsOf(context);
    final duration = disableAnimations ? Duration.zero : tokens.fast;
    final node = widget.node;
    final interactive = widget.ctx.interactive;
    final iconData = (node.icon != null && node.icon!.isNotEmpty
        ? uiIcon(node.icon!)
        : null);

    return Semantics(
      button: true,
      enabled: interactive,
      excludeSemantics: true,
      label: controlNameOf(node.automationName, node.title, node.key),
      child: FocusableActionDetector(
        enabled: interactive,
        mouseCursor: interactive ? SystemMouseCursors.click : MouseCursor.defer,
        onShowFocusHighlight: (f) => setState(() => _focused = f),
        onShowHoverHighlight: (h) => setState(() => _hover = h),
        actions: {
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) {
              _activate();
              return null;
            },
          ),
        },
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: interactive ? _activate : null,
          child: AnimatedContainer(
            duration: duration,
            curve: tokens.ease,
            padding: EdgeInsets.symmetric(
              horizontal: tokens.p12,
              vertical: tokens.p8,
            ),
            decoration: BoxDecoration(
              color: _hover && interactive ? c.menuHover : c.surface,
              borderRadius: BorderRadius.circular(tokens.button),
              border: Border.all(
                color: _focused ? c.focus : c.hairline,
                width: _focused ? 2 : 1,
              ),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                IcAppIcon(appId: node.appId, icon: iconData, size: 32),
                SizedBox(width: tokens.p10),
                Flexible(
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        node.title,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: UiType.textStyle(
                          13,
                          weight: FontWeight.w600,
                          color: c.strong,
                        ),
                      ),
                      if (node.category case final category?
                          when category.isNotEmpty)
                        Text(
                          category,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: UiType.textStyle(11, color: c.muted),
                        ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
