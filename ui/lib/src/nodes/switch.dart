import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/kit/ui_kit_switch.dart';
import 'button.dart';
import 'node_context.dart';

Widget renderSwitch(SwitchNode node, NodeContext ctx) => Builder(
  builder: (context) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      Flexible(child: Text(node.label)),
      const SizedBox(width: 10),
      UiKitSwitch(
        value: node.isOn,
        locked: node.locked,
        semanticLabel: controlNameOf(node.automationName, node.label, node.key),
        onChanged: node.command == null || node.locked || !ctx.interactive
            ? null
            : (on) => pressCommand(
                node.key,
                node.command,
                ctx,
                context,
                value: on ? 'true' : 'false',
              ),
      ),
    ],
  ),
);
