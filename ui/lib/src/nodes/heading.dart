import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';

Widget renderHeading(HeadingNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final c = UiColors.of(context);
    final text = Theme.of(context).textTheme;

    // Level 6+ renders a crisp left-aligned uppercase eyebrow SectionHeader
    // (never crushed by hairline dividers inside narrow sidebars).
    if (node.level >= 6) {
      final eyebrowStyle = UiType.textStyle(
        11,
        weight: FontWeight.w700,
        color: c.muted,
        height: 1.3,
      ).copyWith(letterSpacing: 0.75);
      return Semantics(
        header: true,
        child: Padding(
          padding: const EdgeInsets.only(top: 4, bottom: 2),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Flexible(
                child: Text(
                  node.value.toUpperCase(),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: eyebrowStyle,
                ),
              ),
              if (node.subtitle case final sub? when sub.isNotEmpty) ...[
                const SizedBox(width: 8),
                Text(
                  sub,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: text.labelSmall?.copyWith(color: c.subtle),
                ),
              ],
            ],
          ),
        ),
      );
    }

    final style = switch (node.level) {
      <= 1 => UiType.displayStyle(
        26,
        weight: FontWeight.w700,
        color: c.strong,
      ).copyWith(letterSpacing: -0.55, height: 1.14),
      2 => UiType.displayStyle(
        20,
        weight: FontWeight.w700,
        color: c.strong,
      ).copyWith(letterSpacing: -0.40, height: 1.18),
      3 => UiType.displayStyle(
        16,
        weight: FontWeight.w600,
        color: c.strong,
      ).copyWith(letterSpacing: -0.25, height: 1.22),
      4 => UiType.textStyle(
        14,
        weight: FontWeight.w600,
        color: c.strong,
        height: 1.25,
      ).copyWith(letterSpacing: -0.15),
      _ => UiType.textStyle(
        13,
        weight: FontWeight.w600,
        color: c.strong,
        height: 1.30,
      ).copyWith(letterSpacing: -0.10),
    };

    return Semantics(
      header: true,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        spacing: 2,
        children: [
          Text(
            node.value,
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: style,
          ),
          if (node.subtitle case final subtitle? when subtitle.isNotEmpty)
            Text(
              subtitle,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: text.bodySmall?.copyWith(color: c.muted),
            ),
        ],
      ),
    );
  },
);
