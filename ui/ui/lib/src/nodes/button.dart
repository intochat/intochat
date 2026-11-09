import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/kit/ui_kit_button.dart';

/// A button activates its key on the view; the server routes its emit or
/// returns its intent, which the client performs through the intent gate.
Widget renderButton(ButtonNode node, NodeContext ctx) => Builder(
  builder: (context) => UiKitButton(
    key: ValueKey('button:${node.key}'),
    label: node.label,
    icon: node.icon,
    compact: node.small,
    style: switch (node.style) {
      'primary' => UiKitButtonStyle.primary,
      'ghost' => UiKitButtonStyle.ghost,
      'danger' => UiKitButtonStyle.danger,
      'link' => UiKitButtonStyle.link,
      _ when node.tone == 'accent' => UiKitButtonStyle.primary,
      _ => UiKitButtonStyle.secondary,
    },
    onPressed: node.enabled && ctx.interactive
        ? () => ctx.activate(node.key, context: context)
        : null,
  ),
);
