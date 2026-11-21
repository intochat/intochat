import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../primitives/tabs.dart';

/// Tab items carry a key but no kind: a pick activates the item's key.
Widget renderTabs(TabsNode node, NodeContext ctx) {
  final index = node.items.indexWhere((item) => item.id == node.selectedId);
  return UiMixTabs(
    labels: [for (final item in node.items) item.label],
    index: index < 0 ? 0 : index,
    disabled: ctx.interactive
        ? const {}
        : {for (var i = 0; i < node.items.length; i++) i},
    onChanged: (i) {
      if (ctx.interactive && i != index) ctx.activate(node.items[i].key);
    },
  );
}
