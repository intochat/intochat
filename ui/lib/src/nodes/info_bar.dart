import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_common.dart';
import '../components/infobar/ui_info_bar.dart';
import 'button.dart';

/// An info bar; its close control sends the bar's `close` command the way a
/// button sends its command.
Widget renderInfoBar(InfoBarNode node, NodeContext ctx) => Builder(
  builder: (context) => Semantics(
    label: node.automationName.isEmpty ? null : node.automationName,
    container: true,
    child: UiInfoBar(
      severity: switch (nodeToneOf(node.tone)) {
        UiNodeTone.done => 'success',
        UiNodeTone.working => 'warning',
        UiNodeTone.error => 'error',
        UiNodeTone.accent || UiNodeTone.neutral => 'info',
      },
      title: node.title,
      body: node.message,
      trailing: node.action == null ? null : renderButton(node.action!, ctx),
      onDismiss: node.close == null || !ctx.interactive
          ? null
          : () => pressCommand(node.key, node.close, ctx, context),
    ),
  ),
);
