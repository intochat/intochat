import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_common.dart';
import 'node_context.dart';

/// Children left to right. A row of chips and buttons wraps; a row whose fixed
/// extents do not fit scrolls sideways so every control stays reachable.
Widget renderRow(RowNode node, NodeContext ctx) {
  final children = renderAll(node.children, ctx);
  final extents = node.extents ?? const <double>[];
  final gap = node.gap.clamp(0.0, 128.0);
  final inline = node.children.every(
    (child) =>
        child is BadgeNode || child is TagPillNode || child is ButtonNode,
  );
  if (inline) {
    return Padding(
      padding: EdgeInsets.all(node.padding.clamp(0, 128)),
      child: Wrap(spacing: gap, runSpacing: gap, children: children),
    );
  }
  double? fixed(int i) => switch (extents.elementAtOrNull(i)) {
    final extent? when extent > 1 => extent,
    _ => null,
  };
  final minimum =
      gap * (children.length - 1) +
      [for (var i = 0; i < children.length; i++) fixed(i) ?? 96.0]
          .fold(0.0, (sum, extent) => sum + extent);
  return Padding(
    padding: EdgeInsets.all(node.padding.clamp(0, 128)),
    child: LayoutBuilder(
      builder: (context, constraints) {
        final bounded = constraints.hasBoundedWidth;
        Widget row({required bool flexible}) => Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: flexible ? MainAxisSize.max : MainAxisSize.min,
          spacing: gap,
          children: [
            for (var i = 0; i < children.length; i++)
              if (fixed(i) case final extent?)
                SizedBox(width: extent, child: children[i])
              else if (flexible)
                Expanded(
                  flex: switch (extents.elementAtOrNull(i)) {
                    final extent? when extent > 0 => (extent * 100).round(),
                    _ => 100,
                  },
                  child: children[i],
                )
              else
                children[i],
          ],
        );
        if (!bounded) return row(flexible: false);
        if (constraints.maxWidth >= minimum) return row(flexible: true);
        return SingleChildScrollView(
          primary: false,
          scrollDirection: Axis.horizontal,
          child: SizedBox(width: minimum, child: row(flexible: true)),
        );
      },
    ),
  );
}
