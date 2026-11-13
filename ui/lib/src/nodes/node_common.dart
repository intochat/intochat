import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_renderer.dart';

import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../components/kit/kit_toast.dart';
import '../design/cards/card_shell.dart';

/// Semantic node tone resolved across `Badge`, `TagPill`, `Text`, and `InfoBar`.
enum UiNodeTone { neutral, accent, done, working, error }

/// The kit tone for a wire tone string (`neutral`, `done`, `working`, `error`, ...).
UiTone toneOf(String tone) => switch (nodeToneOf(tone)) {
  UiNodeTone.done => UiTone.done,
  UiNodeTone.working => UiTone.working,
  UiNodeTone.error => UiTone.error,
  UiNodeTone.neutral || UiNodeTone.accent => UiTone.neutral,
};

/// Resolves all C# semantic tone strings (`accent`, `ok`, `info`, `warn`, etc.).
UiNodeTone nodeToneOf(String tone) => switch (tone.toLowerCase()) {
  'accent' || 'primary' || 'info' => UiNodeTone.accent,
  'done' || 'ok' || 'success' || 'positive' => UiNodeTone.done,
  'working' || 'warn' || 'warning' || 'pending' => UiNodeTone.working,
  'error' || 'danger' || 'critical' => UiNodeTone.error,
  _ => UiNodeTone.neutral,
};

/// The card source line for a node's system name.
CardSource cardSource(String name) =>
    CardSource(name, const Icon(LucideIcons.box, size: 14));

/// A child-button callback: activates the button's key when it is enabled and the view is live.
VoidCallback? pressOf(ButtonNode? button, NodeContext ctx) =>
    button == null || !button.enabled || !ctx.interactive
    ? null
    : () => ctx.activate(button.key);

/// The children of a layout node, rendered one level deeper.
List<Widget> renderAll(List<Node> nodes, NodeContext ctx) => [
  for (final node in nodes) NodeRenderer.render(node, ctx.deeper()),
];
