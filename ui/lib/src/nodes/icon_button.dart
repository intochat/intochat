import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../composition/value_nodes/view_menu.dart';
import '../theme/ui_icon.dart';
import 'button.dart';
import 'node_context.dart';

/// An icon-only button: its tooltip names it, and it sends its command the
/// way [renderButton] does.
Widget renderIconButton(IconButtonNode node, NodeContext ctx) => !node.visible
    ? const SizedBox.shrink()
    : Builder(
        builder: (context) => Semantics(
          label: node.automationName.isEmpty ? node.key : node.automationName,
          button: true,
          child: ViewIconButton(
            key: ValueKey('iconButton:${node.key}'),
            icon: uiIcon(node.icon) ?? Icons.circle_outlined,
            label: node.tooltip,
            onPressed: node.enabled && ctx.interactive
                ? () => pressCommand(node.key, node.command, ctx, context)
                : null,
          ),
        ),
      );
