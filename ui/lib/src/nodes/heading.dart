import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';

Widget renderHeading(HeadingNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final c = UiColors.of(context);
    final text = Theme.of(context).textTheme;
    final style = switch (node.level) {
      <= 1 => UiType.displayStyle(
        28,
        weight: FontWeight.w700,
        color: c.strong,
      ).copyWith(letterSpacing: -0.6, height: 1.12),
      2 => UiType.displayStyle(
        24,
        weight: FontWeight.w700,
        color: c.strong,
      ).copyWith(letterSpacing: -0.5, height: 1.15),
      3 => UiType.displayStyle(
        18,
        weight: FontWeight.w600,
        color: c.strong,
      ).copyWith(letterSpacing: -0.35, height: 1.20),
      4 => UiType.textStyle(
        15,
        weight: FontWeight.w600,
        color: c.strong,
        height: 1.25,
      ).copyWith(letterSpacing: -0.25),
      _ => UiType.textStyle(
        13.5,
        weight: FontWeight.w600,
        color: c.strong,
        height: 1.30,
      ).copyWith(letterSpacing: -0.15),
    };
    return Semantics(
      header: true,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(node.value, style: style),
          if (node.subtitle case final subtitle? when subtitle.isNotEmpty)
            Text(subtitle, style: text.bodySmall?.copyWith(color: c.muted)),
        ],
      ),
    );
  },
);
