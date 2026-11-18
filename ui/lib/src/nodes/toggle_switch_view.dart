import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/kit/ui_kit_switch.dart';
import '../generated/node_commands.dart';
import 'node_context.dart';

/// What a toggle switch neuron draws: its value from the neuron's view, its
/// label, on/off texts, enabled and accessible name from the slot's props. A
/// flip sends `SetChecked` to the neuron itself (`.`); the neuron owns the
/// value and redraws. Disabled props send nothing.
Widget renderToggleSwitchView(ToggleSwitchViewNode node, NodeContext ctx) {
  final props = ToggleSwitchProps.fromJson(ctx.slotProps);
  final content = node.isOn ? props.onContent : props.offContent;
  return Row(
    children: [
      Expanded(child: Text(props.label)),
      if (content != null && content.isNotEmpty)
        Padding(padding: const EdgeInsets.only(right: 8), child: Text(content)),
      UiKitSwitch(
        value: node.isOn,
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
