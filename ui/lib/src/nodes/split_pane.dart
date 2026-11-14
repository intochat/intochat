import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../design/tokens.dart';
import 'node_context.dart';
import 'node_renderer.dart';

Widget renderSplitPane(SplitPaneNode node, NodeContext ctx) =>
    _SplitPaneWidget(node: node, ctx: ctx);

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

  @override
  void didUpdateWidget(covariant _SplitPaneWidget oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.node.leadingExtent != widget.node.leadingExtent) {
      _draggedExtent = null;
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final node = widget.node;
    final ctx = widget.ctx;

    return LayoutBuilder(
      builder: (context, constraints) {
        final leading = NodeRenderer.render(node.leading, ctx.deeper());
        final trailing = NodeRenderer.render(node.trailing, ctx.deeper());
        final stretch =
            constraints.hasBoundedHeight && NodeRenderer.fills(node.trailing);
        final gap = node.gap.clamp(0.0, 128.0);
        final requested = (_draggedExtent ?? node.leadingExtent).clamp(
          0.0,
          1200.0,
        );

        if (!constraints.hasBoundedWidth) {
          return Row(
            crossAxisAlignment: stretch
                ? CrossAxisAlignment.stretch
                : CrossAxisAlignment.start,
            spacing: gap,
            children: [
              SizedBox(width: requested, child: leading),
              trailing,
            ],
          );
        }

        final availWidth = constraints.maxWidth;
        final sashWidth = math.max(8.0, gap);
        final minLeading = math.min(80.0, availWidth * 0.25);
        final maxLeading = math.max(
          minLeading,
          availWidth - sashWidth - math.min(120.0, availWidth * 0.25),
        );
        final effectiveLeading = availWidth <= sashWidth + 24
            ? math.max(0.0, (availWidth - sashWidth) * 0.5)
            : requested.clamp(minLeading, maxLeading);

        return Row(
          crossAxisAlignment: stretch
              ? CrossAxisAlignment.stretch
              : CrossAxisAlignment.start,
          children: [
            SizedBox(width: effectiveLeading, child: leading),
            MouseRegion(
              cursor: ctx.interactive
                  ? SystemMouseCursors.resizeLeftRight
                  : MouseCursor.defer,
              onEnter: (_) => setState(() => _hoverSash = true),
              onExit: (_) => setState(() => _hoverSash = false),
              child: GestureDetector(
                behavior: HitTestBehavior.opaque,
                onDoubleTap: ctx.interactive
                    ? () => setState(() => _draggedExtent = null)
                    : null,
                onHorizontalDragStart: ctx.interactive
                    ? (_) => setState(() => _draggingSash = true)
                    : null,
                onHorizontalDragUpdate: ctx.interactive
                    ? (details) {
                        setState(() {
                          final base = _draggedExtent ?? effectiveLeading;
                          _draggedExtent = (base + details.delta.dx).clamp(
                            minLeading,
                            maxLeading,
                          );
                        });
                      }
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
                      duration: IcMotion.fast,
                      width: (_hoverSash || _draggingSash) ? 3 : 1,
                      constraints: const BoxConstraints(minHeight: 24),
                      decoration: BoxDecoration(
                        color: _draggingSash
                            ? c.accent
                            : (_hoverSash
                                  ? c.accent.withValues(alpha: 0.65)
                                  : c.borderHairline),
                        borderRadius: BorderRadius.circular(2),
                      ),
                    ),
                  ),
                ),
              ),
            ),
            Expanded(child: trailing),
          ],
        );
      },
    );
  }
}
