import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/kit/ui_kit_button.dart';
import '../theme/wire_node_values.dart';
import 'node_context.dart';

const _guardArgKeys = [
  'tooltip',
  'disabledReason',
  'guardReason',
  '__guard',
  '_guard',
  'reason',
  'hint',
  'subtitle',
];

String? _resolveButtonHint(ButtonNode node, NodeContext ctx) {
  final fromView = ctx.buttonHints[node.key];
  if (fromView != null && fromView.trim().isNotEmpty) {
    return fromView.trim();
  }
  final args = node.emit?.args;
  if (args is Map) {
    for (final k in _guardArgKeys) {
      final v = args[k];
      if (v is String && v.trim().isNotEmpty) {
        return v.trim();
      }
    }
  }
  return null;
}

/// A button activates its key on the view; the server routes its emit or
/// returns its intent, which the client performs through the intent gate.
/// When guarded (`.Guard(rule)`) or Carrying a hint/reason, renders a
/// `DESIGN.md` §10.8 [Tooltip].
Widget renderButton(ButtonNode node, NodeContext ctx) => Builder(
  builder: (context) => UiKitButton(
    key: ValueKey('button:${node.key}'),
    label: node.label,
    icon: node.icon,
    compact: node.small,
    tooltip: _resolveButtonHint(node, ctx),
    style: parseButtonStyle(node.style, node.tone),
    onPressed: node.enabled && ctx.interactive
        ? () => ctx.activate(node.key, context: context)
        : null,
  ),
);
