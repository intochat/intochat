import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../theme/ui_tokens.dart';
import 'node_context.dart';
import 'node_renderer.dart';

/// Viewport width below which [SplitPaneNode] collapses its leading sidebar
/// behind a compact toggle bar instead of cramping both panes side-by-side.
const double kCompactSplitPaneBreakpoint = 640;

Widget renderSplitPane(SplitPaneNode node, NodeContext ctx) =>
    _SplitPaneWidget(node: node, ctx: ctx);

class _NudgeSashIntent extends Intent {
  const _NudgeSashIntent(this.delta);
  final double delta;
}

class _SplitPaneWidget extends StatefulWidget {
  const _SplitPaneWidget({required this.node, required this.ctx});

  final SplitPaneNode node;
  final NodeContext ctx;

  @override
  State<_SplitPaneWidget> createState() => _SplitPaneWidgetState();
}

class _SplitPaneWidgetState extends State<_SplitPaneWidget> {
  double? _draggedExtent;
  bool _hoverSash = false;
  bool _draggingSash = false;
  bool _focusSash = false;
  bool _compactDrawerOpen = false;
  bool _hoverDrawerToggle = false;
  bool _focusDrawerToggle = false;

  @override
  void didUpdateWidget(covariant _SplitPaneWidget oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.node.leadingExtent != widget.node.leadingExtent) {
      _draggedExtent = null;
    }
  }

  Duration _motion(BuildContext context, Duration base) =>
      MediaQuery.disableAnimationsOf(context) ? Duration.zero : base;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final node = widget.node;
    final ctx = widget.ctx;

    return LayoutBuilder(
      builder: (context, constraints) {
        final leading = NodeRenderer.render(node.leading, ctx.deeper());
        final trailing = NodeRenderer.render(node.trailing, ctx.deeper());
        final stretch = constraints.hasBoundedHeight;
        final leadingScrolls = !NodeRenderer.fills(node.leading);
        final trailingScrolls = !NodeRenderer.fills(node.trailing);
        final gap = node.gap.clamp(0.0, 128.0);
        final requested = (_draggedExtent ?? node.leadingExtent).clamp(
          0.0,
          1200.0,
        );

        final leadingPane = stretch && leadingScrolls
            ? SingleChildScrollView(
                primary: false,
                padding: const EdgeInsets.only(right: 4, bottom: 16),
                child: leading,
              )
            : leading;
        final trailingPane = stretch && trailingScrolls
            ? SingleChildScrollView(
                primary: false,
                padding: const EdgeInsets.only(bottom: 16),
                child: trailing,
              )
            : trailing;

        if (!constraints.hasBoundedWidth) {
          return Row(
            crossAxisAlignment: stretch
                ? CrossAxisAlignment.stretch
                : CrossAxisAlignment.start,
            spacing: gap,
            children: [
              SizedBox(width: requested, child: leadingPane),
              trailingPane,
            ],
          );
        }

        final availWidth = constraints.maxWidth;

        // Compact responsive collapse (< 640px): render a crisp sidebar toggle
        // bar + collapsible sunken drawer above/over the main trailing pane.
        if (availWidth < kCompactSplitPaneBreakpoint) {
          return _buildCompact(
            context,
            c: c,
            leading: leading,
            trailing: trailingPane,
            stretch: stretch,
            interactive: ctx.interactive,
          );
        }

        final sashWidth = math.max(8.0, gap);
        final minLeading = math.min(120.0, availWidth * 0.22);
        final maxLeading = math.max(
          minLeading,
          availWidth - sashWidth - math.min(200.0, availWidth * 0.30),
        );
        final effectiveLeading = availWidth <= sashWidth + 24
            ? math.max(0.0, (availWidth - sashWidth) * 0.5)
            : requested.clamp(minLeading, maxLeading);

        void nudge(double delta) {
          if (!ctx.interactive) return;
          setState(() {
            final base = _draggedExtent ?? effectiveLeading;
            _draggedExtent = (base + delta).clamp(minLeading, maxLeading);
          });
        }

        final sashActive = _hoverSash || _draggingSash || _focusSash;

        return Row(
          crossAxisAlignment: stretch
              ? CrossAxisAlignment.stretch
              : CrossAxisAlignment.start,
          children: [
            SizedBox(width: effectiveLeading, child: leadingPane),
            Semantics(
              key: const ValueKey('split_pane_sash'),
              label: 'Resize sidebar',
              slider: true,
              child: FocusableActionDetector(
                enabled: ctx.interactive,
                onShowFocusHighlight: (f) => setState(() => _focusSash = f),
                onShowHoverHighlight: (h) => setState(() => _hoverSash = h),
                mouseCursor: ctx.interactive
                    ? SystemMouseCursors.resizeLeftRight
                    : MouseCursor.defer,
                shortcuts: const {
                  SingleActivator(LogicalKeyboardKey.arrowLeft):
                      _NudgeSashIntent(-16),
                  SingleActivator(LogicalKeyboardKey.arrowRight):
                      _NudgeSashIntent(16),
                },
                actions: {
                  _NudgeSashIntent: CallbackAction<_NudgeSashIntent>(
                    onInvoke: (intent) {
                      nudge(intent.delta);
                      return null;
                    },
                  ),
                  ActivateIntent: CallbackAction<ActivateIntent>(
                    onInvoke: (_) {
                      setState(() => _draggedExtent = null);
                      return null;
                    },
                  ),
                },
                child: GestureDetector(
                  behavior: HitTestBehavior.opaque,
                  onDoubleTap: ctx.interactive
                      ? () => setState(() => _draggedExtent = null)
                      : null,
                  onHorizontalDragStart: ctx.interactive
                      ? (_) => setState(() => _draggingSash = true)
                      : null,
                  onHorizontalDragUpdate: ctx.interactive
                      ? (details) => nudge(details.delta.dx)
                      : null,
                  onHorizontalDragEnd: ctx.interactive
                      ? (_) => setState(() => _draggingSash = false)
                      : null,
                  onHorizontalDragCancel: ctx.interactive
                      ? () => setState(() => _draggingSash = false)
                      : null,
                  child: SizedBox(
                    width: sashWidth,
                    child: Center(
                      child: AnimatedContainer(
                        duration: _motion(context, IcMotion.fast),
                        width: sashActive ? 3 : 1,
                        constraints: const BoxConstraints(minHeight: 24),
                        decoration: BoxDecoration(
                          color: (_draggingSash || _focusSash)
                              ? c.accent
                              : (_hoverSash
                                    ? c.accent.withValues(alpha: 0.65)
                                    : c.borderHairline),
                          borderRadius: BorderRadius.circular(2),
                          boxShadow: _focusSash
                              ? [
                                  BoxShadow(
                                    color: c.ring,
                                    spreadRadius: 2,
                                    blurRadius: 0,
                                  ),
                                ]
                              : null,
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
            Expanded(child: trailingPane),
          ],
        );
      },
    );
  }

  Widget _buildCompact(
    BuildContext context, {
    required IcColors c,
    required Widget leading,
    required Widget trailing,
    required bool stretch,
    required bool interactive,
  }) {
    void toggleDrawer() {
      if (!interactive) return;
      setState(() => _compactDrawerOpen = !_compactDrawerOpen);
    }

    final headerBar = Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Align(
        alignment: Alignment.centerLeft,
        child: Semantics(
          button: true,
          toggled: _compactDrawerOpen,
          label: _compactDrawerOpen ? 'Hide navigation' : 'Show navigation',
          child: FocusableActionDetector(
            enabled: interactive,
            onShowFocusHighlight: (f) => setState(() => _focusDrawerToggle = f),
            onShowHoverHighlight: (h) => setState(() => _hoverDrawerToggle = h),
            mouseCursor: interactive
                ? SystemMouseCursors.click
                : MouseCursor.defer,
            actions: {
              ActivateIntent: CallbackAction<ActivateIntent>(
                onInvoke: (_) {
                  toggleDrawer();
                  return null;
                },
              ),
            },
            child: GestureDetector(
              key: const ValueKey('split_pane_drawer_toggle'),
              behavior: HitTestBehavior.opaque,
              onTap: interactive ? toggleDrawer : null,
              child: AnimatedContainer(
                duration: _motion(context, IcMotion.fast),
                height: 32,
                padding: const EdgeInsets.symmetric(horizontal: 10),
                decoration: BoxDecoration(
                  color: _compactDrawerOpen
                      ? c.accentTint
                      : (_hoverDrawerToggle ? c.hoverOverlay : c.surfaceCard),
                  borderRadius: BorderRadius.circular(IcRadius.button),
                  border: Border.all(
                    color: _focusDrawerToggle || _compactDrawerOpen
                        ? c.accent
                        : c.borderInput,
                  ),
                  boxShadow: _focusDrawerToggle
                      ? [
                          BoxShadow(
                            color: c.ring,
                            spreadRadius: 3,
                            blurRadius: 0,
                          ),
                        ]
                      : null,
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(
                      LucideIcons.panelLeft,
                      size: 15,
                      color: _compactDrawerOpen ? c.accent : c.textStrong,
                    ),
                    const SizedBox(width: 6),
                    Text(
                      'Navigation',
                      style: IcType.text(
                        12.5,
                        weight: FontWeight.w600,
                        color: _compactDrawerOpen ? c.accent : c.textStrong,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );

    final drawerPanel = _compactDrawerOpen
        ? Container(
            key: const ValueKey('split_pane_compact_drawer'),
            margin: const EdgeInsets.only(bottom: 12),
            padding: const EdgeInsets.all(10),
            constraints: const BoxConstraints(maxHeight: 280),
            decoration: BoxDecoration(
              color: c.surfaceSunk,
              borderRadius: BorderRadius.circular(IcRadius.card),
              border: Border.all(color: c.borderHairline),
            ),
            child: SingleChildScrollView(primary: false, child: leading),
          )
        : const SizedBox.shrink();

    return Column(
      mainAxisSize: stretch ? MainAxisSize.max : MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        headerBar,
        drawerPanel,
        if (stretch) Expanded(child: trailing) else trailing,
      ],
    );
  }
}
