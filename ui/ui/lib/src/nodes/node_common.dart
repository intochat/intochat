import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_renderer.dart';

import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../components/kit/kit_toast.dart';
import '../design/cards/card_shell.dart';

/// The kit tone for a wire tone string (`neutral`, `done`, `working`, `error`, ...).
UiTone toneOf(String tone) => switch (tone.toLowerCase()) {
  'done' || 'success' || 'positive' => UiTone.done,
  'working' || 'warning' || 'pending' => UiTone.working,
  'error' || 'danger' || 'critical' => UiTone.error,
  _ => UiTone.neutral,
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
