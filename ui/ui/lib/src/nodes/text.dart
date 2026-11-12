import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_common.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';

Widget renderText(TextNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final theme = Theme.of(context);
    final colors = theme.extension<UiColors>();
    final rawTone = node.tone.toLowerCase();
    final color = switch (rawTone) {
      'muted' ||
      'subtle' ||
      'ghost' => colors?.muted ?? theme.colorScheme.onSurfaceVariant,
      'strong' => colors?.strong ?? theme.colorScheme.onSurface,
      _ => switch (nodeToneOf(rawTone)) {
        UiNodeTone.accent => colors?.accent ?? theme.colorScheme.primary,
        UiNodeTone.done => colors?.done,
        UiNodeTone.working => colors?.working,
        UiNodeTone.error => colors?.error ?? theme.colorScheme.error,
        UiNodeTone.neutral => null,
      },
    };
    final baseStyle = node.mono
        ? UiType.tabularStyle(
            node.size ?? 12,
            weight: FontWeight.w600,
            color: color ?? theme.textTheme.bodyMedium?.color,
          ).copyWith(letterSpacing: -0.1)
        : (theme.textTheme.bodyMedium ?? const TextStyle(fontSize: 13));
    return SelectableText(
      node.value,
      style: baseStyle.copyWith(
        color: color,
        fontWeight: rawTone == 'strong'
            ? FontWeight.w600
            : (node.mono ? FontWeight.w600 : null),
        fontSize: node.size,
        fontFeatures: (node.tabular || node.mono) ? UiType.tabular : null,
      ),
    );
  },
);
