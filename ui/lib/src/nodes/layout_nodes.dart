import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'node_common.dart';
import 'node_context.dart';
import 'node_renderer.dart';

// --- src/nodes/spacer.dart ---

Widget renderSpacer(SpacerNode node, NodeContext ctx) =>
    SizedBox.square(dimension: node.size.clamp(0, 512));

// --- src/nodes/divider.dart ---
Widget renderDivider(DividerNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final c = UiColors.of(context);
    final line = Container(height: 1, color: c.hairline);
    final label = node.label ?? '';
    if (label.isEmpty) return line;
    return Row(
      children: [
        Expanded(child: line),
        const SizedBox(width: 14),
        Flexible(
          child: Text(
            label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: UiType.of(context).bodyMuted.copyWith(fontSize: 13),
          ),
        ),
        const SizedBox(width: 14),
        Expanded(child: line),
      ],
    );
  },
);

// --- src/nodes/grid.dart ---
const double kCompactGridBreakpoint = 640;
const double _kSingleColumnGridBreakpoint = 400;

Widget renderGrid(GridNode node, NodeContext ctx) {
  final children = renderAll(node.children, ctx);
  final requestedColumns = node.columns.clamp(1, 12);
  final gap = node.gap.clamp(0.0, 128.0);
  return LayoutBuilder(
    builder: (context, constraints) {
      if (!constraints.hasBoundedWidth) {
        return Wrap(
          spacing: gap,
          runSpacing: gap,
          children: [
            for (final child in children) SizedBox(width: 240, child: child),
          ],
        );
      }

      final maxW = math.max(0.0, constraints.maxWidth);
      final effectiveColumns = switch (maxW) {
        < _kSingleColumnGridBreakpoint => 1,
        < kCompactGridBreakpoint => math.min(requestedColumns, 2),
        _ => requestedColumns,
      };
      final rawWidth = (maxW - gap * (effectiveColumns - 1)) / effectiveColumns;
      final width = math.max(0.0, rawWidth);

      return Wrap(
        spacing: gap,
        runSpacing: gap,
        children: [
          for (final child in children) SizedBox(width: width, child: child),
        ],
      );
    },
  );
}

// --- src/nodes/column.dart ---
bool _isIntrinsicWidthControl(Node node) =>
    node is ButtonNode || node is TagPillNode || node is BadgeNode;

/// Children top to bottom (`VStack` / `Column`).
///
/// Extent encoding (`node.extents[i]`):
/// - `> 1`: fixed pixel height `SizedBox(height: extent)`.
/// - `0 < extent <= 1`: proportional vertical `Expanded` when height is bounded.
/// - `-1`: explicit full-width stretch (`SizedBox(width: double.infinity)`).
/// - `-2`: horizontal center alignment.
/// - `-3`: horizontal end alignment.
/// - `-4`: horizontal start alignment.
/// - Unannotated buttons, pills, and badges size to their content (`centerStart`)
///   instead of stretching into full-window bars; cards/surfaces/inputs stretch.
Widget renderColumn(ColumnNode node, NodeContext ctx) {
  final children = renderAll(node.children, ctx);
  final extents = node.extents ?? const <double>[];
  final pad = EdgeInsets.all(node.padding.clamp(0.0, 128.0));
  final gap = node.gap.clamp(0.0, 128.0);

  return Padding(
    padding: pad,
    child: LayoutBuilder(
      builder: (context, constraints) {
        final boundedHeight =
            constraints.hasBoundedHeight && constraints.maxHeight.isFinite;
        final hasExplicitFlex = extents.any((e) => e > 0 && e <= 1);
        int? autoFillIdx;
        if (boundedHeight &&
            constraints.maxHeight >= 220 &&
            !hasExplicitFlex &&
            node.children.length <= 4) {
          for (var i = 0; i < node.children.length; i++) {
            if (NodeRenderer.fills(node.children[i])) {
              if (autoFillIdx == null && extents.elementAtOrNull(i) == null) {
                autoFillIdx = i;
              } else {
                autoFillIdx = null;
                break;
              }
            }
          }
        }
        final hasVerticalFlex =
            boundedHeight && (hasExplicitFlex || autoFillIdx != null);

        Widget wrapChild(int i) {
          final child = children[i];
          final rawNode = node.children[i];
          final ext = extents.elementAtOrNull(i);

          if (autoFillIdx == i) {
            return Expanded(child: child);
          }

          if (ext != null) {
            if (ext > 1) {
              return SizedBox(height: ext, child: child);
            }
            if (ext > 0 && ext <= 1) {
              return boundedHeight
                  ? Expanded(
                      flex: math.max(1, (ext * 100).round()),
                      child: child,
                    )
                  : child;
            }
            if (ext == -1) {
              return SizedBox(width: double.infinity, child: child);
            }
            if (ext == -2) {
              return Align(alignment: Alignment.center, child: child);
            }
            if (ext == -3) {
              return Align(
                alignment: AlignmentDirectional.centerEnd,
                child: child,
              );
            }
            if (ext == -4) {
              return Align(
                alignment: AlignmentDirectional.centerStart,
                child: child,
              );
            }
          }

          if (_isIntrinsicWidthControl(rawNode)) {
            return Align(
              alignment: AlignmentDirectional.centerStart,
              child: child,
            );
          }

          return child;
        }

        return Column(
          mainAxisSize: hasVerticalFlex ? MainAxisSize.max : MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: gap,
          children: [for (var i = 0; i < children.length; i++) wrapChild(i)],
        );
      },
    ),
  );
}

// --- src/nodes/row.dart ---
bool _isInlineControl(Node n) =>
    n is BadgeNode ||
    n is TagPillNode ||
    n is ButtonNode ||
    n is IconButtonNode;

bool _isCenterAlignedRowChild(Node n) =>
    _isInlineControl(n) ||
    n is HeadingNode ||
    n is TextNode ||
    n is SpacerNode ||
    n is FieldNode;

bool _isFlexSpacer(Node n) => n is SpacerNode && n.size <= 0;

/// Children left to right (`HStack` / `HeaderBar` / `Row`).
///
/// Layout rules (WinUI / CSS flex parity):
/// - Pure button/pill/badge/switch rows wrap cleanly on narrow viewports.
/// - `Spacer(size <= 0)` (`FlexSpacer()`) expands to push trailing items right.
/// - `extents[i] > 1`: fixed pixel width `SizedBox(width: extent)`.
/// - `0 < extents[i] <= 1`: proportional flex `Expanded(flex: ...)`.
/// - Unannotated children size to their natural content (`Auto`), never stretched
///   into equal-width columns; text/headings shrink gracefully with `Flexible`
///   when space is tight so rows never overflow.
Widget renderRow(RowNode node, NodeContext ctx) {
  final extents = node.extents ?? const <double>[];
  final gap = node.gap.clamp(0.0, 128.0);
  final pad = EdgeInsets.all(node.padding.clamp(0.0, 128.0));

  if (node.children.isEmpty) {
    return Padding(padding: pad, child: const SizedBox.shrink());
  }

  final hasExplicitExtents = extents.any((e) => e > 0);
  final hasFlexSpacer = node.children.any(_isFlexSpacer);

  // Pure inline chip/button/toggle row without flex spacers wraps naturally.
  if (!hasExplicitExtents &&
      !hasFlexSpacer &&
      node.children.every(_isInlineControl)) {
    final children = renderAll(node.children, ctx);
    return Padding(
      padding: pad,
      child: Wrap(
        spacing: gap,
        runSpacing: math.min(gap, 8.0),
        crossAxisAlignment: WrapCrossAlignment.center,
        children: children,
      ),
    );
  }

  final renderedChildren = renderAll(node.children, ctx);

  double? fixedWidth(int i) => switch (extents.elementAtOrNull(i)) {
    final extent? when extent > 1 => extent,
    _ => null,
  };

  int? flexWeight(int i) => switch (extents.elementAtOrNull(i)) {
    final extent? when extent > 0 && extent <= 1 => math.max(
      1,
      (extent * 100).round(),
    ),
    _ => null,
  };

  return Padding(
    padding: pad,
    child: LayoutBuilder(
      builder: (context, constraints) {
        final boundedWidth = constraints.hasBoundedWidth;
        final stretchHeight =
            constraints.hasBoundedHeight &&
            node.children.any(NodeRenderer.fills);
        final centerCross =
            !stretchHeight && node.children.every(_isCenterAlignedRowChild);

        Widget wrapVertical(int i, Widget child) =>
            stretchHeight && !NodeRenderer.fills(node.children[i])
            ? SingleChildScrollView(primary: false, child: child)
            : child;

        final hasAnyExpanded =
            boundedWidth &&
            (hasFlexSpacer ||
                [for (var i = 0; i < node.children.length; i++) flexWeight(i)]
                    .any((f) => f != null));

        // When every child in a bounded Row is a card/surface/stat without
        // explicit extents (e.g. side-by-side equal cards `HStack(CardA, CardB)`),
        // distribute equally so multi-card rows fill width cleanly.
        final allBlockCards =
            boundedWidth &&
            !hasAnyExpanded &&
            !hasExplicitExtents &&
            node.children.length > 1 &&
            node.children.every(
              (c) =>
                  c is CardNode ||
                  c is SurfaceNode ||
                  c is StatCardNode ||
                  c is MetricTileNode ||
                  c is ChartNode ||
                  c is DataTableNode,
            );

        if (allBlockCards) {
          final minCardsWidth =
              gap * (node.children.length - 1) + node.children.length * 180.0;
          if (constraints.maxWidth < minCardsWidth) {
            return Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: gap,
              children: renderedChildren,
            );
          }
          return Row(
            crossAxisAlignment: stretchHeight
                ? CrossAxisAlignment.stretch
                : CrossAxisAlignment.start,
            spacing: gap,
            children: [
              for (var i = 0; i < renderedChildren.length; i++)
                Expanded(child: wrapVertical(i, renderedChildren[i])),
            ],
          );
        }

        final rowChildren = <Widget>[];
        for (var i = 0; i < node.children.length; i++) {
          final childNode = node.children[i];
          if (_isFlexSpacer(childNode)) {
            rowChildren.add(
              boundedWidth ? const Spacer() : SizedBox(width: math.max(8, gap)),
            );
            continue;
          }
          if (childNode is SpacerNode) {
            rowChildren.add(SizedBox(width: childNode.size.clamp(0.0, 256.0)));
            continue;
          }

          final w = wrapVertical(i, renderedChildren[i]);
          if (fixedWidth(i) case final fw?) {
            rowChildren.add(SizedBox(width: fw, child: w));
          } else if (boundedWidth && flexWeight(i) != null) {
            rowChildren.add(Expanded(flex: flexWeight(i)!, child: w));
          } else if (boundedWidth &&
              (childNode is HeadingNode ||
                  childNode is TextNode ||
                  childNode is TabStripViewNode)) {
            rowChildren.add(Flexible(fit: FlexFit.loose, child: w));
          } else if (boundedWidth &&
              childNode is FieldNode &&
              !hasAnyExpanded) {
            rowChildren.add(Expanded(child: w));
          } else {
            rowChildren.add(w);
          }
        }

        final builtRow = Row(
          mainAxisSize: boundedWidth && hasAnyExpanded
              ? MainAxisSize.max
              : MainAxisSize.min,
          crossAxisAlignment: stretchHeight
              ? CrossAxisAlignment.stretch
              : (centerCross
                    ? CrossAxisAlignment.center
                    : CrossAxisAlignment.start),
          spacing: gap,
          children: rowChildren,
        );

        if (!boundedWidth || hasAnyExpanded) {
          return builtRow;
        }

        return SingleChildScrollView(
          primary: false,
          scrollDirection: Axis.horizontal,
          child: ConstrainedBox(
            constraints: const BoxConstraints(minWidth: 0),
            child: builtRow,
          ),
        );
      },
    ),
  );
}

// --- src/nodes/collection.dart ---
const int kVirtualizedCollectionThreshold = 32;

Widget renderCollection(CollectionNode node, NodeContext ctx) {
  if (node.entries.isEmpty) {
    return Builder(
      builder: (context) {
        final c = UiColors.of(context);
        return Padding(
          padding: const EdgeInsets.symmetric(vertical: 12),
          child: Text(
            'Nothing here yet.',
            style: UiType.textStyle(13.5, color: c.textMuted),
          ),
        );
      },
    );
  }

  if (node.entries.length < kVirtualizedCollectionThreshold) {
    return Builder(
      builder: (context) {
        final c = UiColors.of(context);
        return Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            for (var i = 0; i < node.entries.length; i++)
              _CollectionRow(
                entry: node.entries[i],
                colors: c,
                showDivider: i > 0,
              ),
          ],
        );
      },
    );
  }

  return LayoutBuilder(
    builder: (context, constraints) {
      final c = UiColors.of(context);
      final list = ListView.builder(
        key: const ValueKey('virtualized_collection_list'),
        primary: false,
        itemCount: node.entries.length,
        itemBuilder: (context, index) => _CollectionRow(
          entry: node.entries[index],
          colors: c,
          showDivider: index > 0,
        ),
      );
      if (constraints.hasBoundedHeight) {
        return list;
      }
      return ConstrainedBox(
        constraints: const BoxConstraints(maxHeight: 480),
        child: list,
      );
    },
  );
}

class _CollectionRow extends StatelessWidget {
  const _CollectionRow({
    required this.entry,
    required this.colors,
    required this.showDivider,
  });

  final Entry entry;
  final UiColors colors;
  final bool showDivider;

  @override
  Widget build(BuildContext context) {
    final subtitle = entry.subtitle;
    final trailing = entry.trailing;
    return DecoratedBox(
      key: ValueKey(entry.id),
      decoration: BoxDecoration(
        border: showDivider
            ? Border(top: BorderSide(color: colors.borderHairline))
            : null,
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 8, horizontal: 2),
        child: Row(
          children: [
            Expanded(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    entry.title,
                    style: UiType.textStyle(
                      13.5,
                      weight: FontWeight.w500,
                      color: colors.textStrong,
                    ),
                  ),
                  if (subtitle != null && subtitle.isNotEmpty) ...[
                    const SizedBox(height: 2),
                    Text(
                      subtitle,
                      style: UiType.textStyle(12.5, color: colors.textMuted),
                    ),
                  ],
                ],
              ),
            ),
            if (trailing != null && trailing.isNotEmpty) ...[
              const SizedBox(width: 12),
              Text(
                trailing,
                style: UiType.textStyle(
                  12.5,
                  color: colors.textMuted,
                  tnum: true,
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

// --- src/nodes/split_pane.dart ---
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
  bool _trailingOverlayOpen = true;

  @override
  void didUpdateWidget(covariant _SplitPaneWidget oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.node.leadingExtent != widget.node.leadingExtent ||
        oldWidget.node.trailingFraction != widget.node.trailingFraction) {
      _draggedExtent = null;
    }
    if (widget.node.trailingDrawer == true &&
        (oldWidget.node.trailingDrawer != true ||
            oldWidget.node.trailing != widget.node.trailing)) {
      _trailingOverlayOpen = true;
    }
  }

  Duration _motion(BuildContext context, Duration base) =>
      MediaQuery.disableAnimationsOf(context) ? Duration.zero : base;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
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
        final isTrailingDrawer = node.trailingDrawer == true;
        final collapseAt =
            node.collapseBreakpoint ??
            (isTrailingDrawer ? 1024.0 : kCompactSplitPaneBreakpoint);

        if (isTrailingDrawer && availWidth < collapseAt) {
          return _buildTrailingOverlay(
            context,
            c: c,
            availWidth: availWidth,
            leading: leadingPane,
            trailing: trailingPane,
            interactive: ctx.interactive,
          );
        }

        // Compact responsive collapse (< 640px): render a crisp sidebar toggle
        // bar + collapsible sunken drawer above/over the main trailing pane.
        if (availWidth < collapseAt) {
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
        final minLeading = math.min(
          isTrailingDrawer ? 280.0 : 120.0,
          availWidth * 0.28,
        );
        final maxLeading = math.max(
          minLeading,
          availWidth - sashWidth - math.min(200.0, availWidth * 0.30),
        );
        final defaultLeading = isTrailingDrawer && node.trailingFraction != null
            ? (availWidth - sashWidth) *
                  (1.0 - node.trailingFraction!.clamp(0.35, 0.72))
            : requested;
        final effectiveLeading = availWidth <= sashWidth + 24
            ? math.max(0.0, (availWidth - sashWidth) * 0.5)
            : (_draggedExtent ?? defaultLeading).clamp(minLeading, maxLeading);

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
                        duration: _motion(context, UiTokens.of(context).fast),
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
    required UiColors c,
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
                duration: _motion(context, UiTokens.of(context).fast),
                height: 32,
                padding: const EdgeInsets.symmetric(horizontal: 10),
                decoration: BoxDecoration(
                  color: _compactDrawerOpen
                      ? c.accentTint
                      : (_hoverDrawerToggle ? c.hoverOverlay : c.surfaceCard),
                  borderRadius: BorderRadius.circular(
                    UiTokens.of(context).button,
                  ),
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
                      style: UiType.textStyle(
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
              borderRadius: BorderRadius.circular(UiTokens.of(context).card),
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

  Widget _buildTrailingOverlay(
    BuildContext context, {
    required UiColors c,
    required double availWidth,
    required Widget leading,
    required Widget trailing,
    required bool interactive,
  }) {
    final panelWidth = math.min(availWidth * 0.92, 560.0);
    return Stack(
      children: [
        Positioned.fill(child: leading),
        if (!_trailingOverlayOpen)
          Positioned(
            top: 10,
            right: 12,
            child: Semantics(
              button: true,
              label: 'Open artifact drawer',
              child: MouseRegion(
                cursor: interactive
                    ? SystemMouseCursors.click
                    : MouseCursor.defer,
                child: GestureDetector(
                  key: const ValueKey('split_pane_trailing_toggle'),
                  onTap: interactive
                      ? () => setState(() => _trailingOverlayOpen = true)
                      : null,
                  child: Container(
                    height: 30,
                    padding: const EdgeInsets.symmetric(horizontal: 10),
                    decoration: BoxDecoration(
                      color: c.accentTint,
                      borderRadius: BorderRadius.circular(
                        UiTokens.of(context).sm,
                      ),
                      border: Border.all(
                        color: c.accent.withValues(alpha: 0.4),
                      ),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(LucideIcons.panelRight, size: 14, color: c.accent),
                        const SizedBox(width: 6),
                        Text(
                          'Artifact',
                          style: UiType.textStyle(
                            12,
                            weight: FontWeight.w600,
                            color: c.accent,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ),
        if (_trailingOverlayOpen) ...[
          Positioned.fill(
            child: GestureDetector(
              key: const ValueKey('split_pane_trailing_scrim'),
              behavior: HitTestBehavior.opaque,
              onTap: interactive
                  ? () => setState(() => _trailingOverlayOpen = false)
                  : null,
              child: ColoredBox(color: c.textStrong.withValues(alpha: 0.18)),
            ),
          ),
          Positioned(
            top: 0,
            right: 0,
            bottom: 0,
            width: panelWidth,
            child: Container(
              key: const ValueKey('split_pane_trailing_overlay'),
              decoration: BoxDecoration(
                color: c.surfaceElevated,
                border: Border(left: BorderSide(color: c.borderHairline)),
              ),
              child: trailing,
            ),
          ),
        ],
      ],
    );
  }
}
