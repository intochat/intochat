import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../generated/node_commands.dart';
import '../primitives/primitives.dart';
import 'node_context.dart';

/// What a toggle switch neuron draws: its value from the neuron's view, its
/// label, on/off texts, enabled and accessible name from the child's props. A
/// flip sends `SetChecked` to the neuron itself (`.`); the neuron owns the
/// value and redraws. Disabled props send nothing.
Widget renderToggleSwitchView(ToggleSwitchViewNode node, NodeContext ctx) {
  final props = ToggleSwitchProps.fromJson(ctx.childProps);
  // A controlled switch draws the parent's value; otherwise the neuron's.
  final isOn = props.isOn ?? node.isOn;
  final content = isOn ? props.onContent : props.offContent;
  return Row(
    children: [
      Expanded(child: Text(props.label)),
      if (content != null && content.isNotEmpty)
        Padding(padding: const EdgeInsets.only(right: 8), child: Text(content)),
      UiMixToggle(
        value: isOn,
        enabled: props.isEnabled && ctx.interactive,
        semanticLabel: controlNameOf(
          props.automationName,
          props.label,
          node.key,
        ),
        onChanged: props.isEnabled && ctx.interactive
            ? (on) => actToggleSwitchSetChecked(ctx, '.', isOn: on)
            : null,
      ),
    ],
  );
}
