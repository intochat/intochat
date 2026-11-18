import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../generated/node_commands.dart';
import 'node_context.dart';

/// What a check box neuron draws: its value from the neuron's view, its label,
/// enabled and accessible name from the props its parent wrote on the slot.
/// A tap sends `Toggle` to the neuron itself (`.`); the neuron owns the value
/// and redraws, so nothing is kept here. Disabled props send nothing.
Widget renderCheckBoxView(CheckBoxViewNode node, NodeContext ctx) {
  final props = CheckBoxProps.fromJson(ctx.slotProps);
  final enabled = props.isEnabled && ctx.interactive;
  return Semantics(
    label: controlNameOf(props.automationName, props.label, node.key),
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
            Flexible(child: Text(props.label)),
          ],
        ),
      ),
    ),
  );
}
