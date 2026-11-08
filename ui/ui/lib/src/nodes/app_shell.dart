import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_renderer.dart';

Widget renderAppShell(AppShellNode node, NodeContext ctx) => LayoutBuilder(
  builder: (context, constraints) {
    final inner = ctx.deeper();
    final main = Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: 12,
      children: [
        if (node.toolbar case final toolbar?)
          NodeRenderer.render(toolbar, inner),
        NodeRenderer.render(node.body, inner),
      ],
    );
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      spacing: 16,
      children: [
        if (node.sidebar case final sidebar?)
          SizedBox(width: 240, child: NodeRenderer.render(sidebar, inner)),
        if (constraints.hasBoundedWidth) Expanded(child: main) else main,
        if (node.inspector case final inspector?)
          SizedBox(width: 280, child: NodeRenderer.render(inspector, inner)),
      ],
    );
  },
);
