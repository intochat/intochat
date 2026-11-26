import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../primitives/button.dart';
import '../primitives/style.dart';
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

/// A button sends its command through [pressCommand], or performs its
/// client-local intent. A refusal is shown by the host. When guarded
/// (`.Guard(rule)`) or carrying a tooltip, hint or reason, renders a
/// `DESIGN.md` §10.8 [Tooltip].
///
/// Supports both legacy string presets (`"primary"`, `"secondary"`, `"ghost"`,
/// `"danger"`, `"link"`) via [UiKitButton] and structured C# `Mix.Style()` JSON
/// objects via [UiMixButton].
Widget renderButton(ButtonNode node, NodeContext ctx) => !node.visible
    ? const SizedBox.shrink()
    : Builder(
        builder: (context) {
          final interactive = node.enabled && ctx.interactive;
          final onPressed = interactive
              ? () => pressCommand(
                  node.key,
                  node.command,
                  ctx,
                  context,
                  intent: node.intent,
                )
              : null;
          final tooltip = _resolveButtonHint(node, ctx);
          final rawStyle = node.style;
          final structuredStyle = rawStyle is Map
              ? UiStyleData.fromWire(rawStyle)
              : null;

          final child = structuredStyle != null
              ? UiMixButton(
                  key: ValueKey('button:${node.key}'),
                  label: node.label,
                  icon: node.icon,
                  tooltip: tooltip,
                  style: structuredStyle,
                  enabled: interactive,
                  onPressed: onPressed,
                )
              : UiKitButton(
                  key: ValueKey('button:${node.key}'),
                  label: node.label,
                  icon: node.icon,
                  compact: node.small,
                  tooltip: tooltip,
                  style: parseButtonStyle(
                    rawStyle is String ? rawStyle : '',
                    node.tone,
                  ),
                  onPressed: onPressed,
                );

          return Semantics(
            label: node.automationName.isEmpty ? node.key : node.automationName,
            button: true,
            child: child,
          );
        },
      );

/// Sends a control's [command] through [NodeContext.act] (`POST /act`), the
/// one route of a person's action, with the child-aware target and a field's
/// [value] beside the args (the server adds it as `value` and the screen
/// stamps who acted). A node with no command performs its client-local
/// [intent] itself; the server never sees it. [key] names the node for the
/// host's refusal message only.
Future<ActivationOutcome> pressCommand(
  String key,
  Command? command,
  NodeContext ctx,
  BuildContext? context, {
  String? value,
  IntentNode? intent,
}) async {
  if (command == null || command.target.isEmpty || command.signal.isEmpty) {
    if (intent == null || !ctx.interactive) return ActivationOutcome.refused;
    final performed = await ctx.perform(
      intent,
      context: context != null && context.mounted ? context : null,
    );
    return performed ? ActivationOutcome.accepted : ActivationOutcome.refused;
  }
  return ctx.act(
    command.target,
    command.signal,
    command.args ?? const <String, Object?>{},
    value: value,
    context: context,
  );
}
