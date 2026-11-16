import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/progress/ui_progress.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import 'node_context.dart';

Widget renderProgress(ProgressNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final c = UiColors.of(context);
    final determinate = !node.value.isNaN && node.value >= 0;
    final normalized = determinate
        ? (node.value > 1 ? node.value / 100 : node.value).clamp(0.0, 1.0)
        : 0.0;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        UiProgress(
          determinate: determinate,
          value: normalized,
          label: node.label,
        ),
        if (node.detail case final detail? when detail.isNotEmpty) ...[
          const SizedBox(height: 4),
          Text(detail, style: UiType.tabularStyle(12.5, color: c.muted)),
        ],
      ],
    );
  },
);
