import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../composition/value_nodes/view_badge.dart';
import 'node_common.dart';

Widget renderTagPill(TagPillNode node, NodeContext ctx) =>
    ViewBadge(text: node.label, tone: toneOf(node.tone).index, icon: node.icon);
