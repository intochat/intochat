import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../composition/value_nodes/view_menu.dart';
import '../primitives/icon_button.dart';
import '../primitives/style.dart';
import '../theme/ui_icon.dart';
import 'button.dart';
import 'node_context.dart';

/// An icon-only button: its tooltip names it, and it sends its command the
/// way [renderButton] does.
Widget renderIconButton(IconButtonNode node, NodeContext ctx) => !node.visible
    ? const SizedBox.shrink()
    : Builder(
        builder: (context) {
          final interactive = node.enabled && ctx.interactive;
          final onPressed = interactive
              ? () => pressCommand(
                  node.key,
                  node.command,
                  ctx,
                  context,
                  intent: node.intent,
                )
              : null;
          final iconData = uiIcon(node.icon) ?? Icons.circle_outlined;
          final structuredStyle = node.style is Map
              ? UiStyleData.fromWire(node.style)
              : null;
          final child = structuredStyle != null
              ? UiMixIconButton(
                  key: ValueKey('iconButton:${node.key}'),
                  icon: iconData,
                  tooltip: node.tooltip,
                  enabled: interactive,
                  style: structuredStyle,
                  onPressed: onPressed,
                )
              : ViewIconButton(
                  key: ValueKey('iconButton:${node.key}'),
                  icon: iconData,
                  label: node.tooltip,
                  onPressed: onPressed,
                );
          return Semantics(
            label: node.automationName.isEmpty ? node.key : node.automationName,
            button: true,
            child: child,
          );
        },
      );
