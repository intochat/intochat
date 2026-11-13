import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_common.dart';

Widget renderGrid(GridNode node, NodeContext ctx) {
  final children = renderAll(node.children, ctx);
  final columns = node.columns.clamp(1, 12);
  final gap = node.gap.clamp(0.0, 128.0);
  return LayoutBuilder(
    builder: (context, constraints) {
      final width = constraints.hasBoundedWidth
          ? (constraints.maxWidth - gap * (columns - 1)) / columns
          : 240.0;
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
