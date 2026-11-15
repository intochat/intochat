import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../design/tokens.dart';
import 'node_context.dart';
import 'node_renderer.dart';
import 'split_pane.dart';

Widget renderAppShell(AppShellNode node, NodeContext ctx) => LayoutBuilder(
  builder: (context, constraints) {
    final c = IcTheme.of(context);
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

    final compact =
        constraints.hasBoundedWidth &&
        constraints.maxWidth < kCompactSplitPaneBreakpoint;
    if (compact) {
      return Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 12,
        children: [
          if (node.sidebar case final sidebar?)
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: c.surfaceSunk,
                borderRadius: BorderRadius.circular(IcRadius.card),
                border: Border.all(color: c.borderHairline),
              ),
              child: NodeRenderer.render(sidebar, inner),
            ),
          main,
          if (node.inspector case final inspector?)
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: c.surfaceSunk,
                borderRadius: BorderRadius.circular(IcRadius.card),
                border: Border.all(color: c.borderHairline),
              ),
              child: NodeRenderer.render(inspector, inner),
            ),
        ],
      );
    }

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
