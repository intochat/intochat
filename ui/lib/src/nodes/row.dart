import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_common.dart';
import 'node_context.dart';
import 'node_renderer.dart';

bool _isInlineControl(Node n) =>
    n is BadgeNode ||
    n is TagPillNode ||
    n is ButtonNode ||
    n is SwitchNode;

bool _isCenterAlignedRowChild(Node n) =>
    _isInlineControl(n) ||
    n is HeadingNode ||
    n is TextNode ||
    n is SpacerNode ||
    n is SelectNode ||
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
    final extent? when extent > 0 && extent <= 1 =>
      math.max(1, (extent * 100).round()),
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
                [
                  for (var i = 0; i < node.children.length; i++) flexWeight(i),
                ].any((f) => f != null));

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
              (childNode is HeadingNode || childNode is TextNode)) {
            rowChildren.add(Flexible(fit: FlexFit.loose, child: w));
          } else if (boundedWidth && childNode is FieldNode && !hasAnyExpanded) {
            rowChildren.add(Expanded(child: w));
          } else {
            rowChildren.add(w);
          }
        }

        final builtRow = Row(
          mainAxisSize:
              boundedWidth && hasAnyExpanded ? MainAxisSize.max : MainAxisSize.min,
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
