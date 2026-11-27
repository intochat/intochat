import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../components/workspace/ui_workspace_components.dart';
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
import 'layout_nodes.dart';
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

// --- src/nodes/app_shell.dart ---
const double kShellSidebarDefaultWidth = 248;
const double kShellSidebarMinWidth = 180;
const double kShellSidebarMaxWidth = 360;

const double kShellInspectorDefaultWidth = 320;
const double kShellInspectorMinWidth = 260;
const double kShellInspectorMaxWidth = 640;

const double _kShellSashHitWidth = 8;

/// Studio-grade desktop application shell (`AppShellNode`):
/// - Flush, compact `42px`–`48px` top toolbar header with 1-click sidebar and
///   inspector collapse/expand buttons (`key:shell-toggle-sidebar`,
///   `key:shell-toggle-inspector`).
/// - Draggable & keyboard-adjustable resize sashes on `sidebar` (`180–360px`,
///   default `248px`) and `inspector` (`260–640px`, default `320px`).
/// - Smooth `180ms` animated body transitions (`fade-through`, `shared-axis-x`,
///   `shared-axis-z`, `camera-fly-to`) respecting reduced-motion preferences.
/// - Clean vertical stack fallback on compact viewports (`< 640px`).
Widget renderAppShell(AppShellNode node, NodeContext ctx) => Semantics(
  container: true,
  explicitChildNodes: true,
  label: controlNameOf(node.automationName, node.key, node.key),
  child: _AppShellWidget(node: node, ctx: ctx),
);

class _NudgeShellSashIntent extends Intent {
  const _NudgeShellSashIntent(this.delta);
  final double delta;
}

class _AppShellWidget extends StatefulWidget {
  const _AppShellWidget({required this.node, required this.ctx});

  final AppShellNode node;
  final NodeContext ctx;

  @override
  State<_AppShellWidget> createState() => _AppShellWidgetState();
}

class _AppShellWidgetState extends State<_AppShellWidget> {
  double _sidebarWidth = kShellSidebarDefaultWidth;
  double _inspectorWidth = kShellInspectorDefaultWidth;
  bool _sidebarCollapsed = false;
  bool _inspectorCollapsed = false;

  bool _hoverSidebarSash = false;
  bool _draggingSidebarSash = false;
  bool _focusSidebarSash = false;

  bool _hoverInspectorSash = false;
  bool _draggingInspectorSash = false;
  bool _focusInspectorSash = false;

  Duration _motion(BuildContext context, Duration base) =>
      MediaQuery.disableAnimationsOf(context) ? Duration.zero : base;

  Widget _buildPaneToggle({
    required UiColors c,
    required String identifier,
    required String buttonKey,
    required IconData icon,
    required bool expanded,
    required String expandLabel,
    required String collapseLabel,
    required bool enabled,
    required VoidCallback onToggle,
  }) {
    final label = expanded ? collapseLabel : expandLabel;
    return Semantics(
      key: ValueKey(buttonKey),
      identifier: identifier,
      button: true,
      enabled: enabled,
      toggled: expanded,
      label: label,
      onTap: enabled ? onToggle : null,
      child: ExcludeSemantics(
        child: Tooltip(
          message: label,
          child: Material(
            color: Colors.transparent,
            child: InkWell(
              borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
              onTap: enabled ? onToggle : null,
              child: Container(
                width: 28,
                height: 28,
                decoration: BoxDecoration(
                  color: expanded ? c.accentTint : Colors.transparent,
                  borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
                  border: Border.all(
                    color: expanded
                        ? c.accent.withValues(alpha: 0.35)
                        : c.borderHairline,
                  ),
                ),
                child: Center(
                  child: Icon(
                    icon,
                    size: 14,
                    color: expanded ? c.accent : c.textMuted,
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildSash(
    BuildContext context, {
    required UiColors c,
    required String sashKey,
    required String identifier,
    required String label,
    required bool active,
    required bool dragging,
    required bool focused,
    required bool hovered,
    required bool interactive,
    required ValueChanged<bool> onFocus,
    required ValueChanged<bool> onHover,
    required ValueChanged<bool> onDragState,
    required ValueChanged<double> onDelta,
    required VoidCallback onReset,
  }) {
    return Semantics(
      key: ValueKey(sashKey),
      identifier: identifier,
      label: label,
      slider: true,
      child: FocusableActionDetector(
        enabled: interactive,
        onShowFocusHighlight: onFocus,
        onShowHoverHighlight: onHover,
        mouseCursor: interactive
            ? SystemMouseCursors.resizeLeftRight
            : MouseCursor.defer,
        shortcuts: const {
          SingleActivator(LogicalKeyboardKey.arrowLeft): _NudgeShellSashIntent(
            -16,
          ),
          SingleActivator(LogicalKeyboardKey.arrowRight): _NudgeShellSashIntent(
            16,
          ),
        },
        actions: {
          _NudgeShellSashIntent: CallbackAction<_NudgeShellSashIntent>(
            onInvoke: (intent) {
              onDelta(intent.delta);
              return null;
            },
          ),
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) {
              onReset();
              return null;
            },
          ),
        },
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onDoubleTap: interactive ? onReset : null,
          onHorizontalDragStart: interactive ? (_) => onDragState(true) : null,
          onHorizontalDragUpdate: interactive
              ? (details) => onDelta(details.delta.dx)
              : null,
          onHorizontalDragEnd: interactive ? (_) => onDragState(false) : null,
          onHorizontalDragCancel: interactive ? () => onDragState(false) : null,
          child: SizedBox(
            width: _kShellSashHitWidth,
            child: Center(
              child: AnimatedContainer(
                duration: _motion(context, UiTokens.of(context).fast),
                width: active ? 3 : 1,
                constraints: const BoxConstraints(minHeight: 24),
                decoration: BoxDecoration(
                  color: (dragging || focused)
                      ? c.accent
                      : (hovered
                            ? c.accent.withValues(alpha: 0.65)
                            : c.borderHairline),
                  borderRadius: BorderRadius.circular(2),
                  boxShadow: focused
                      ? [BoxShadow(color: c.ring, spreadRadius: 2)]
                      : null,
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final node = widget.node;
    final ctx = widget.ctx;
    final inner = ctx.deeper();

    return LayoutBuilder(
      builder: (context, constraints) {
        final boundedHeight =
            constraints.hasBoundedHeight && constraints.maxHeight.isFinite;
        final boundedWidth =
            constraints.hasBoundedWidth && constraints.maxWidth.isFinite;
        final bodyWidget = NodeRenderer.render(node.body, inner);
        final bodyFills = NodeRenderer.fills(node.body);

        final compact =
            boundedWidth && constraints.maxWidth < kCompactSplitPaneBreakpoint;

        if (compact) {
          final stacked = Padding(
            padding: const EdgeInsets.fromLTRB(8, 6, 8, 10),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: 8,
              children: [
                if (node.toolbar case final toolbar?)
                  NodeRenderer.render(toolbar, inner),
                if (node.sidebar case final sidebar?)
                  Container(
                    padding: const EdgeInsets.all(8),
                    decoration: BoxDecoration(
                      color: c.surfaceSunk,
                      borderRadius: BorderRadius.circular(
                        UiTokens.of(context).card,
                      ),
                      border: Border.all(color: c.borderHairline),
                    ),
                    child: NodeRenderer.render(sidebar, inner),
                  ),
                bodyWidget,
                if (node.inspector case final inspector?)
                  Container(
                    padding: const EdgeInsets.all(8),
                    decoration: BoxDecoration(
                      color: c.surfaceSunk,
                      borderRadius: BorderRadius.circular(
                        UiTokens.of(context).card,
                      ),
                      border: Border.all(color: c.borderHairline),
                    ),
                    child: NodeRenderer.render(inspector, inner),
                  ),
              ],
            ),
          );
          return boundedHeight
              ? SingleChildScrollView(primary: false, child: stacked)
              : stacked;
        }

        Widget scrollPane(
          Node paneNode,
          Widget rendered, {
          EdgeInsets? padding,
        }) {
          if (!boundedHeight || NodeRenderer.fills(paneNode)) {
            return rendered;
          }
          return SingleChildScrollView(
            primary: false,
            padding: padding ?? const EdgeInsets.only(bottom: 10),
            child: rendered,
          );
        }

        final transition = parseViewTransition(node.transition);
        final bodySignature = shellBodyTransitionSignature(
          node.body,
          node.transition,
        );

        final Widget rawMain = boundedHeight
            ? (bodyFills
                  ? bodyWidget
                  : SingleChildScrollView(
                      primary: false,
                      padding: const EdgeInsets.only(bottom: 10),
                      child: bodyWidget,
                    ))
            : bodyWidget;

        final Widget mainArea = UiViewTransition(
          transition: transition,
          signature: bodySignature,
          child: rawMain,
        );

        final hasSidebar = node.sidebar != null;
        final hasInspector = node.inspector != null;
        final showSidebar = hasSidebar && !_sidebarCollapsed;
        final showInspector = hasInspector && !_inspectorCollapsed;

        // Compute clamped side rail widths so body always retains healthy space.
        final contentAvailWidth = boundedWidth
            ? math.max(0.0, constraints.maxWidth - 16.0)
            : 1440.0;
        final minBodyReserve = math.min(220.0, contentAvailWidth * 0.32);
        var maxSideBudget = math.max(
          0.0,
          contentAvailWidth -
              minBodyReserve -
              (showSidebar ? _kShellSashHitWidth : 0.0) -
              (showInspector ? _kShellSashHitWidth : 0.0),
        );

        double effectiveSidebar = 0;
        if (showSidebar) {
          final rawSide = _sidebarWidth.clamp(
            kShellSidebarMinWidth,
            kShellSidebarMaxWidth,
          );
          final cap = showInspector ? maxSideBudget * 0.46 : maxSideBudget;
          effectiveSidebar = cap >= kShellSidebarMinWidth
              ? rawSide.clamp(kShellSidebarMinWidth, cap)
              : math.max(0.0, cap);
          maxSideBudget = math.max(0.0, maxSideBudget - effectiveSidebar);
        }

        double effectiveInspector = 0;
        if (showInspector) {
          final rawInsp = _inspectorWidth.clamp(
            kShellInspectorMinWidth,
            kShellInspectorMaxWidth,
          );
          effectiveInspector = maxSideBudget >= kShellInspectorMinWidth
              ? rawInsp.clamp(
                  kShellInspectorMinWidth,
                  math.min(kShellInspectorMaxWidth, maxSideBudget),
                )
              : math.max(0.0, maxSideBudget);
        }

        final sidebarToggle = hasSidebar
            ? _buildPaneToggle(
                c: c,
                identifier: 'key:shell-toggle-sidebar',
                buttonKey: 'shell-toggle-sidebar',
                icon: LucideIcons.panelLeft,
                expanded: !_sidebarCollapsed,
                expandLabel: 'Expand sidebar',
                collapseLabel: 'Collapse sidebar',
                enabled: ctx.enabled,
                onToggle: () =>
                    setState(() => _sidebarCollapsed = !_sidebarCollapsed),
              )
            : null;

        final inspectorToggle = hasInspector
            ? _buildPaneToggle(
                c: c,
                identifier: 'key:shell-toggle-inspector',
                buttonKey: 'shell-toggle-inspector',
                icon: LucideIcons.panelRight,
                expanded: !_inspectorCollapsed,
                expandLabel: 'Expand inspector',
                collapseLabel: 'Collapse inspector',
                enabled: ctx.enabled,
                onToggle: () =>
                    setState(() => _inspectorCollapsed = !_inspectorCollapsed),
              )
            : null;

        final panesRow = Row(
          crossAxisAlignment: boundedHeight
              ? CrossAxisAlignment.stretch
              : CrossAxisAlignment.start,
          children: [
            if (showSidebar) ...[
              SizedBox(
                key: const ValueKey('shell-sidebar-pane'),
                width: effectiveSidebar,
                child: Padding(
                  padding: const EdgeInsets.only(right: 6),
                  child: scrollPane(
                    node.sidebar!,
                    NodeRenderer.render(node.sidebar!, inner),
                  ),
                ),
              ),
              _buildSash(
                context,
                c: c,
                sashKey: 'shell_sidebar_sash',
                identifier: 'key:shell-sash-sidebar',
                label: 'Resize sidebar',
                active:
                    _hoverSidebarSash ||
                    _draggingSidebarSash ||
                    _focusSidebarSash,
                dragging: _draggingSidebarSash,
                focused: _focusSidebarSash,
                hovered: _hoverSidebarSash,
                interactive: ctx.enabled,
                onFocus: (f) => setState(() => _focusSidebarSash = f),
                onHover: (h) => setState(() => _hoverSidebarSash = h),
                onDragState: (d) => setState(() => _draggingSidebarSash = d),
                onDelta: (dx) {
                  if (!ctx.enabled) return;
                  setState(() {
                    _sidebarWidth = (effectiveSidebar + dx).clamp(
                      kShellSidebarMinWidth,
                      kShellSidebarMaxWidth,
                    );
                  });
                },
                onReset: () =>
                    setState(() => _sidebarWidth = kShellSidebarDefaultWidth),
              ),
              const SizedBox(width: 6),
            ],
            if (boundedWidth) Expanded(child: mainArea) else mainArea,
            if (showInspector) ...[
              const SizedBox(width: 6),
              _buildSash(
                context,
                c: c,
                sashKey: 'shell_inspector_sash',
                identifier: 'key:shell-sash-inspector',
                label: 'Resize inspector',
                active:
                    _hoverInspectorSash ||
                    _draggingInspectorSash ||
                    _focusInspectorSash,
                dragging: _draggingInspectorSash,
                focused: _focusInspectorSash,
                hovered: _hoverInspectorSash,
                interactive: ctx.enabled,
                onFocus: (f) => setState(() => _focusInspectorSash = f),
                onHover: (h) => setState(() => _hoverInspectorSash = h),
                onDragState: (d) => setState(() => _draggingInspectorSash = d),
                onDelta: (dx) {
                  if (!ctx.enabled) return;
                  // Inspector sits on the right rail: dragging left (-dx) widens it.
                  setState(() {
                    _inspectorWidth = (effectiveInspector - dx).clamp(
                      kShellInspectorMinWidth,
                      kShellInspectorMaxWidth,
                    );
                  });
                },
                onReset: () => setState(
                  () => _inspectorWidth = kShellInspectorDefaultWidth,
                ),
              ),
              SizedBox(
                key: const ValueKey('shell-inspector-pane'),
                width: effectiveInspector,
                child: Padding(
                  padding: const EdgeInsets.only(left: 6),
                  child: scrollPane(
                    node.inspector!,
                    NodeRenderer.render(node.inspector!, inner),
                  ),
                ),
              ),
            ],
          ],
        );

        final isEdgeToEdge =
            node.edgeToEdge ||
            (node.body is WebBrowserNode &&
                node.sidebar == null &&
                node.inspector == null);
        final paddedPanes = isEdgeToEdge
            ? panesRow
            : Padding(
                padding: const EdgeInsets.fromLTRB(8, 6, 8, 8),
                child: panesRow,
              );

        if (node.toolbar == null &&
            sidebarToggle == null &&
            inspectorToggle == null) {
          return paddedPanes;
        }

        final headerBar = Container(
          key: const ValueKey('shell-header-bar'),
          constraints: const BoxConstraints(minHeight: 40),
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            border: Border(bottom: BorderSide(color: c.borderHairline)),
          ),
          child: Row(
            mainAxisSize: boundedWidth ? MainAxisSize.max : MainAxisSize.min,
            children: [
              if (sidebarToggle != null) ...[
                sidebarToggle,
                const SizedBox(width: 6),
              ],
              if (node.toolbar != null)
                if (boundedWidth)
                  Expanded(child: NodeRenderer.render(node.toolbar!, inner))
                else
                  NodeRenderer.render(node.toolbar!, inner)
              else if (boundedWidth)
                const Spacer(),
              if (inspectorToggle != null) ...[
                const SizedBox(width: 6),
                inspectorToggle,
              ],
            ],
          ),
        );

        return Column(
          mainAxisSize: boundedHeight ? MainAxisSize.max : MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            headerBar,
            if (boundedHeight) Expanded(child: paddedPanes) else paddedPanes,
          ],
        );
      },
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
