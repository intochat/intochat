import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/kit/ui_kit_select.dart';

/// Options carry a key but no kind: a pick activates the option's key.
Widget renderSelect(SelectNode node, NodeContext ctx) {
  final selected = node.options
      .where((option) => option.id == node.selected)
      .firstOrNull;
  return UiKitSelect<String>(
    label: node.label,
    value: selected?.key,
    items: [
      for (final option in node.options)
        UiKitSelectItem(option.key, option.label, enabled: option.emit != null),
    ],
    onChanged: ctx.interactive
        ? (key) {
            if (key != selected?.key) ctx.activate(key);
          }
        : null,
  );
}
