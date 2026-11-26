import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import 'node_context.dart';

/// Hairline divider; when labelled, the label sits centred in muted 13 with a
/// 1px hairline on either side (gap 14).
Widget renderDivider(DividerNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final c = UiColors.of(context);
    final line = Container(height: 1, color: c.hairline);
    final label = node.label ?? '';
    if (label.isEmpty) return line;
    return Row(
      children: [
        Expanded(child: line),
        const SizedBox(width: 14),
        Flexible(
          child: Text(
            label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: UiType.of(context).bodyMuted.copyWith(fontSize: 13),
          ),
        ),
        const SizedBox(width: 14),
        Expanded(child: line),
      ],
    );
  },
);
