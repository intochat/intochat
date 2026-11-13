import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';

Widget renderSpacer(SpacerNode node, NodeContext ctx) =>
    SizedBox.square(dimension: node.size.clamp(0, 512));
