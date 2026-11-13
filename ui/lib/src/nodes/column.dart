import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_common.dart';

Widget renderColumn(ColumnNode node, NodeContext ctx) {
  final children = renderAll(node.children, ctx);
  final extents = node.extents ?? const <double>[];
  return Padding(
    padding: EdgeInsets.all(node.padding.clamp(0, 128)),
    child: Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: node.gap.clamp(0, 128),
      children: [
        for (var i = 0; i < children.length; i++)
          if (extents.elementAtOrNull(i) case final extent? when extent > 1)
            SizedBox(height: extent, child: children[i])
          else
            children[i],
      ],
    ),
  );
}
