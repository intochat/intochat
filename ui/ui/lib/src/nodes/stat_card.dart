import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/kit/ui_kit_surface.dart';
import '../composition/value_nodes/view_badge.dart';

Widget renderStatCard(StatCardNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final text = Theme.of(context).textTheme;
    return UiKitSurface(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        spacing: 4,
        children: [
          Text(node.label, style: text.bodySmall),
          Text(
            node.value,
            style: text.headlineSmall?.copyWith(
              fontFeatures: const [FontFeature.tabularFigures()],
            ),
          ),
          if (node.delta case final delta?)
            ViewBadge(
              text: delta,
              tone: switch (node.tone) {
                BadgeTone.done => 1,
                BadgeTone.working => 2,
                BadgeTone.error => 3,
                _ => 0,
              },
            ),
        ],
      ),
    );
  },
);
