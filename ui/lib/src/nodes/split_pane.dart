import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_renderer.dart';

Widget renderSplitPane(SplitPaneNode node, NodeContext ctx) => LayoutBuilder(
  builder: (context, constraints) {
    final leading = NodeRenderer.render(node.leading, ctx.deeper());
    final trailing = NodeRenderer.render(node.trailing, ctx.deeper());
    final stretch =
        constraints.hasBoundedHeight && NodeRenderer.fills(node.trailing);
    return Row(
      crossAxisAlignment: stretch
          ? CrossAxisAlignment.stretch
          : CrossAxisAlignment.start,
      spacing: node.gap.clamp(0, 128),
      children: [
        SizedBox(width: node.leadingExtent.clamp(0, 1200), child: leading),
        if (constraints.hasBoundedWidth)
          Expanded(child: trailing)
        else
          trailing,
      ],
    );
  },
);
