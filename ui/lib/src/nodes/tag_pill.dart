import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../primitives/badge.dart';
import 'node_common.dart';

Widget renderTagPill(TagPillNode node, NodeContext ctx) => UiBadge.fromWire(
  label: node.label,
  tone: nodeToneOf(node.tone),
  icon: node.icon,
);
