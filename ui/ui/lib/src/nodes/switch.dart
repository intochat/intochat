import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/kit/ui_kit_switch.dart';

Widget renderSwitch(SwitchNode node, NodeContext ctx) => Row(
  children: [
    Expanded(child: Text(node.label)),
    UiKitSwitch(
      value: node.isOn,
      locked: node.locked,
      semanticLabel: node.label,
      onChanged: node.emit == null || node.locked || !ctx.interactive
          ? null
          : (on) => ctx.activate(node.key, value: on ? 'true' : 'false'),
    ),
  ],
);
