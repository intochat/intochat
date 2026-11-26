import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../primitives/badge.dart';
import 'node_context.dart';

/// Status badge (`DESIGN.md` §2.5): pairs semantic status tones (`done`/`success`,
/// `working`/`warning`, `error`/`danger`, `needs-you`/`accent`, `idle`/`neutral`)
/// with their authoritative icons — never bare colour dots.
Widget renderBadge(BadgeNode node, NodeContext ctx) => UiBadge.fromWire(
  label: node.label,
  tone: node.tone,
  icon: node.icon,
  statusBadge: true,
);
