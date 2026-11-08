import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../theme/ui_colors.dart';

Widget renderText(TextNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final theme = Theme.of(context);
    final colors = theme.extension<UiColors>();
    final color = switch (node.tone) {
      'muted' => colors?.muted ?? theme.colorScheme.onSurfaceVariant,
      'accent' => theme.colorScheme.primary,
      'done' => colors?.done,
      'working' => colors?.working,
      'error' => colors?.error ?? theme.colorScheme.error,
      _ => null,
    };
    return SelectableText(
      node.value,
      style: theme.textTheme.bodyMedium?.copyWith(
        color: color,
        fontWeight: node.tone == 'strong' ? FontWeight.w600 : null,
        fontSize: node.size,
        fontFamily: node.mono ? 'monospace' : null,
        fontFeatures: node.tabular
            ? const [FontFeature.tabularFigures()]
            : null,
      ),
    );
  },
);
