import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../generated/node_commands.dart';
import 'node_context.dart';

/// What a check box neuron draws. A tap sends `Toggle` to the neuron itself
/// (`.`); the neuron owns the value and redraws, so nothing is kept here.
Widget renderCheckBoxView(CheckBoxViewNode node, NodeContext ctx) {
  final enabled = node.isEnabled && ctx.interactive;
  return Semantics(
    label: node.automationName.isEmpty ? node.key : node.automationName,
    checked: node.isOn,
    enabled: enabled,
    child: ExcludeSemantics(
      child: InkWell(
        onTap: enabled ? () => actCheckBoxToggle(ctx, '.') : null,
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Checkbox(
              value: node.isOn,
              onChanged: enabled ? (_) => actCheckBoxToggle(ctx, '.') : null,
            ),
            Flexible(child: Text(node.label)),
          ],
        ),
      ),
    ),
  );
}
