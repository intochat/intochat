import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_common.dart';
import '../components/kit/ui_kit_surface.dart';
import '../composition/value_nodes/view_badge.dart';
import '../theme/ui_theme.dart';

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
            style: UiType.displayStyle(
              28,
              weight: FontWeight.w800,
              color: text.headlineSmall?.color,
            ).copyWith(fontFeatures: UiType.tabular),
          ),
          if (node.delta case final delta?)
            ViewBadge(
              text: delta,
              tone: switch (node.tone) {
                BadgeTone.accent => UiNodeTone.accent,
                BadgeTone.done => UiNodeTone.done,
                BadgeTone.working => UiNodeTone.working,
                BadgeTone.error => UiNodeTone.error,
                BadgeTone.neutral => UiNodeTone.neutral,
              },
            ),
        ],
      ),
    );
  },
);
