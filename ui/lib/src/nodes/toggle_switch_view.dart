import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/kit/ui_kit_switch.dart';
import '../generated/node_commands.dart';
import 'node_context.dart';

/// What a toggle switch neuron draws. A flip sends `SetChecked` to the neuron
/// itself (`.`); the neuron owns the value and redraws.
Widget renderToggleSwitchView(ToggleSwitchViewNode node, NodeContext ctx) {
  final content = node.isOn ? node.onContent : node.offContent;
  return Row(
    children: [
      Expanded(child: Text(node.label)),
      if (content != null && content.isNotEmpty)
        Padding(padding: const EdgeInsets.only(right: 8), child: Text(content)),
      UiKitSwitch(
        value: node.isOn,
        semanticLabel: node.automationName.isEmpty
            ? node.key
            : node.automationName,
        onChanged: node.isEnabled && ctx.interactive
            ? (on) => actToggleSwitchSetChecked(ctx, '.', isOn: on)
            : null,
      ),
    ],
  );
}
