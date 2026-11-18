import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../generated/node_commands.dart';
import 'node_common.dart';
import 'node_context.dart';

/// What an expander neuron draws: a header that sends `Expand` to the neuron
/// itself (`.`), and its children while expanded. The neuron owns whether it
/// is expanded and redraws.
Widget renderExpanderView(ExpanderViewNode node, NodeContext ctx) {
  final enabled = ctx.interactive;
  return Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    mainAxisSize: MainAxisSize.min,
    children: [
      Semantics(
        label: node.automationName.isEmpty ? node.key : node.automationName,
        button: true,
        expanded: node.isExpanded,
        child: ExcludeSemantics(
          child: InkWell(
            onTap: enabled
                ? () => actExpanderExpand(
                    ctx,
                    '.',
                    isExpanded: !node.isExpanded,
                  )
                : null,
            child: Padding(
              padding: const EdgeInsets.symmetric(vertical: 8),
              child: Row(
                children: [
                  Icon(
                    node.isExpanded ? Icons.expand_less : Icons.expand_more,
                    size: 18,
                  ),
                  const SizedBox(width: 8),
                  Expanded(child: Text(node.header)),
                ],
              ),
            ),
          ),
        ),
      ),
      if (node.isExpanded)
        Padding(
          padding: const EdgeInsets.only(left: 26),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            children: renderAll(node.children, ctx),
          ),
        ),
    ],
  );
}
