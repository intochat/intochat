import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_common.dart';

/// Responsive breakpoint below which multi-column grids collapse cleanly so
/// cards and tiles never cramp or overflow narrow viewports (`UK-8`).
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
      final rawWidth =
          (maxW - gap * (effectiveColumns - 1)) / effectiveColumns;
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
