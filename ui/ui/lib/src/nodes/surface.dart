import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/kit/ui_kit_surface.dart';
import 'node_common.dart';

Widget renderSurface(SurfaceNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final theme = Theme.of(context);
    final colors = theme.colorScheme;
    return UiKitSurface(
      padding: EdgeInsets.all(node.padding.clamp(0, 128)),
      color: switch (node.surfaceKind) {
        'recessed' => colors.surfaceContainerHighest,
        'elevated' => colors.surfaceContainerHigh,
        _ => null,
      },
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 12,
        children: [
          if (node.title case final title? when title.isNotEmpty)
            Text(title, style: theme.textTheme.titleMedium),
          if (node.subtitle case final subtitle? when subtitle.isNotEmpty)
            Text(subtitle, style: theme.textTheme.bodySmall),
          ...renderAll(node.children, ctx),
        ],
      ),
    );
  },
);
