import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_common.dart';
import 'node_context.dart';

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
        final boundedHeight = constraints.hasBoundedHeight;
        final hasVerticalFlex =
            boundedHeight && extents.any((e) => e > 0 && e <= 1);

        Widget wrapChild(int i) {
          final child = children[i];
          final rawNode = node.children[i];
          final ext = extents.elementAtOrNull(i);

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
