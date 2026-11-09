import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/infobar/ui_info_bar.dart';
import 'button.dart';

Widget renderInfoBar(InfoBarNode node, NodeContext ctx) => Column(
  mainAxisSize: MainAxisSize.min,
  crossAxisAlignment: CrossAxisAlignment.stretch,
  spacing: 8,
  children: [
    UiInfoBar(
      severity: switch (node.tone) {
        'done' || 'success' => 'success',
        'working' || 'warning' => 'warning',
        'error' => 'error',
        _ => 'info',
      },
      title: node.title,
      body: node.message,
    ),
    if (node.action case final action?)
      Align(
        alignment: AlignmentDirectional.centerStart,
        child: renderButton(action, ctx),
      ),
  ],
);
