import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/progress/ui_progress.dart';

Widget renderProgress(ProgressNode node, NodeContext ctx) => Column(
  mainAxisSize: MainAxisSize.min,
  crossAxisAlignment: CrossAxisAlignment.stretch,
  children: [
    UiProgress(
      determinate: true,
      value: node.value > 1 ? node.value / 100 : node.value,
      label: node.label,
    ),
    if (node.detail case final detail? when detail.isNotEmpty) Text(detail),
  ],
);
