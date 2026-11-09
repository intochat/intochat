import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';

Widget renderHeading(HeadingNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final text = Theme.of(context).textTheme;
    final style = switch (node.level) {
      <= 1 => text.headlineSmall,
      2 => text.titleLarge,
      3 => text.titleMedium,
      _ => text.titleSmall,
    };
    return Semantics(
      header: true,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(node.value, style: style),
          if (node.subtitle case final subtitle? when subtitle.isNotEmpty)
            Text(subtitle, style: text.bodySmall),
        ],
      ),
    );
  },
);
