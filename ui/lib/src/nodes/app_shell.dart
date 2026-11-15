import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../design/tokens.dart';
import 'node_context.dart';
import 'node_renderer.dart';
import 'split_pane.dart';

/// Studio-grade desktop application shell (`AppShellNode`):
/// - Optional pinned top `toolbar` above `body` (never scrolls off-screen).
/// - Independent vertical scrolling for `sidebar`, `body`, and `inspector`
///   when hosted inside a bounded window viewport.
/// - Clean vertical stack fallback on compact viewports (`< 640px`).
Widget renderAppShell(AppShellNode node, NodeContext ctx) => LayoutBuilder(
  builder: (context, constraints) {
    final c = IcTheme.of(context);
    final inner = ctx.deeper();
    final boundedHeight = constraints.hasBoundedHeight;
    final bodyWidget = NodeRenderer.render(node.body, inner);
    final bodyFills = NodeRenderer.fills(node.body);

    final compact =
        constraints.hasBoundedWidth &&
        constraints.maxWidth < kCompactSplitPaneBreakpoint;

    if (compact) {
      final stacked = Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 12,
        children: [
          if (node.toolbar case final toolbar?)
            NodeRenderer.render(toolbar, inner),
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
          bodyWidget,
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
      return boundedHeight
          ? SingleChildScrollView(
              primary: false,
              padding: const EdgeInsets.only(bottom: 16),
              child: stacked,
            )
          : stacked;
    }

    Widget scrollPane(Node paneNode, Widget rendered, {EdgeInsets? padding}) {
      if (!boundedHeight || NodeRenderer.fills(paneNode)) {
        return rendered;
      }
      return SingleChildScrollView(
        primary: false,
        padding: padding ?? const EdgeInsets.only(bottom: 16),
        child: rendered,
      );
    }

    final Widget mainColumn;
    if (boundedHeight) {
      final scrollableBody = bodyFills
          ? bodyWidget
          : SingleChildScrollView(
              primary: false,
              padding: const EdgeInsets.only(bottom: 16),
              child: bodyWidget,
            );
      mainColumn = Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 12,
        children: [
          if (node.toolbar case final toolbar?)
            NodeRenderer.render(toolbar, inner),
          Expanded(child: scrollableBody),
        ],
      );
    } else {
      mainColumn = Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 12,
        children: [
          if (node.toolbar case final toolbar?)
            NodeRenderer.render(toolbar, inner),
          bodyWidget,
        ],
      );
    }

    return Row(
      crossAxisAlignment: boundedHeight
          ? CrossAxisAlignment.stretch
          : CrossAxisAlignment.start,
      spacing: 16,
      children: [
        if (node.sidebar case final sidebar?)
          Container(
            width: 248,
            padding: const EdgeInsets.only(right: 12),
            decoration: BoxDecoration(
              border: Border(right: BorderSide(color: c.borderHairline)),
            ),
            child: scrollPane(sidebar, NodeRenderer.render(sidebar, inner)),
          ),
        if (constraints.hasBoundedWidth)
          Expanded(child: mainColumn)
        else
          mainColumn,
        if (node.inspector case final inspector?)
          Container(
            width: 288,
            padding: const EdgeInsets.only(left: 12),
            decoration: BoxDecoration(
              border: Border(left: BorderSide(color: c.borderHairline)),
            ),
            child: scrollPane(inspector, NodeRenderer.render(inspector, inner)),
          ),
      ],
    );
  },
);
