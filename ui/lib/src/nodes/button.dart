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
  final tooltip = node.tooltip;
  if (tooltip != null && tooltip.trim().isNotEmpty) {
    return tooltip.trim();
  }
  final args = node.command?.args;
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

/// A button sends its command. On a top-level view a command to `.` is
/// sent by activating the button's key, so the server routes it to the
/// view's own neuron whatever kind of app owns it (and returns an intent
/// for a button that has one); a command inside a slot, or to a child
/// address, goes through [NodeContext.act] with the slot-aware target. A
/// refusal is shown by the host. When guarded (`.Guard(rule)`) or carrying a
/// tooltip, hint or reason, renders a `DESIGN.md` §10.8 [Tooltip].
Widget renderButton(ButtonNode node, NodeContext ctx) => !node.visible
    ? const SizedBox.shrink()
    : Builder(
        builder: (context) => Semantics(
          label: node.automationName.isEmpty ? node.key : node.automationName,
          button: true,
          child: UiKitButton(
            key: ValueKey('button:${node.key}'),
            label: node.label,
            icon: node.icon,
            compact: node.small,
            tooltip: _resolveButtonHint(node, ctx),
            style: parseButtonStyle(node.style, node.tone),
            onPressed: node.enabled && ctx.interactive
                ? () => pressCommand(node.key, node.command, ctx, context)
                : null,
          ),
        ),
      );

/// Sends a control's [command]: on a top-level view a command to `.` activates
/// the [key] (unless the view acts via `/act` with a canonical dotted signal,
/// map args, and no field [value]); anything else goes through [NodeContext.act].
Future<void> pressCommand(
  String key,
  Command? command,
  NodeContext ctx,
  BuildContext? context, {
  String? value,
}) {
  final isCanonicalAct =
      command != null &&
      command.target.isNotEmpty &&
      command.signal.contains('.') &&
      (command.args == null || command.args is Map);
  if (command == null ||
      command.target.isEmpty ||
      (ctx.isTopLevelSelf(command.target) &&
          (!ctx.actsViaAct || !isCanonicalAct || value != null))) {
    return ctx.activate(key, value: value, context: context);
  }
  final args = command.args;
  final baseArgs = args is Map
      ? args.cast<String, Object?>()
      : const <String, Object?>{};
  return ctx.act(
    command.target,
    command.signal,
    value == null ? baseArgs : <String, Object?>{...baseArgs, 'value': value},
    context: context,
  );
}
