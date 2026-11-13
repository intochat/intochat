import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../composition/value_nodes/view_divider.dart';

Widget renderDivider(DividerNode node, NodeContext ctx) =>
    ViewDivider(label: node.label ?? '');
