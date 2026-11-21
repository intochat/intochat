import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'button.dart';
import 'node_context.dart';
import 'node_renderer.dart';

import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../ui/mix/ui_mix_toast.dart';
import '../design/cards/card_shell.dart';
import '../theme/brand_visuals.dart';
import '../theme/wire_node_values.dart';

export '../theme/wire_node_values.dart' show UiNodeTone;

/// The kit tone for a wire tone string (`neutral`, `done`, `working`, `error`, ...).
UiTone toneOf(String tone) => switch (nodeToneOf(tone)) {
  UiNodeTone.done => UiTone.done,
  UiNodeTone.working => UiTone.working,
  UiNodeTone.error => UiTone.error,
  UiNodeTone.neutral || UiNodeTone.accent => UiTone.neutral,
};

/// Resolves all C# semantic tone strings (`accent`, `ok`, `info`, `warn`, `needs-you`, `idle`, etc.).
UiNodeTone nodeToneOf(String tone) => parseUiNodeTone(tone);

/// The card source line for a node's system name, upgraded with allowlisted
/// brand/provider/capability icons emitted by C# `VisualCatalog`.
CardSource cardSource(
  String name, {
  String? iconKey,
  String? capabilityLabel,
  bool approvalFallbackShield = false,
}) {
  final preferredLabel =
      (capabilityLabel != null && capabilityLabel.trim().isNotEmpty)
      ? capabilityLabel.trim()
      : null;
  final displayLabel =
      preferredLabel ?? (isDigitalBrainSourceName(name) ? '' : name.trim());
  final resolved = BrandVisuals.tryForIconKey(iconKey, label: displayLabel);
  if (resolved != null) {
    return CardSource(
      displayLabel.isEmpty ? resolved.label : displayLabel,
      IcBrandIcon(spec: resolved, size: 15),
    );
  }
  if (approvalFallbackShield) {
    final shield = BrandVisuals.fromNode(
      label: displayLabel,
      iconKey: 'capability:shield',
    );
    return CardSource(
      displayLabel.isEmpty ? 'Approval' : displayLabel,
      IcBrandIcon(spec: shield, size: 15),
    );
  }
  return CardSource(
    displayLabel.isEmpty ? name : displayLabel,
    const Icon(LucideIcons.box, size: 14),
  );
}

/// A child-button callback: sends the button's command (see [pressCommand]) when it is enabled and the view is live.
VoidCallback? pressOf(ButtonNode? button, NodeContext ctx) =>
    button == null || !button.enabled || !ctx.interactive
    ? null
    : () => pressCommand(button.key, button.command, ctx, null);

/// The children of a layout node, rendered one level deeper.
List<Widget> renderAll(List<Node> nodes, NodeContext ctx) => [
  for (final node in nodes) NodeRenderer.render(node, ctx.deeper()),
];

/// Tags a rendered control with its wire `key` (so tests and automation find it by
/// `Key(node.key)`, and a browser by its semantics identifier `key:<key>`) and, when it
/// declares one, its `automationName` as the semantics label.
Widget keyedControl(String? key, Widget child, {String? automationName}) {
  var wrapped = child;
  if (automationName != null && automationName.isNotEmpty) {
    wrapped = Semantics(label: automationName, container: true, child: wrapped);
  }
  return key == null || key.isEmpty
      ? wrapped
      : KeyedSubtree(
          key: ValueKey(key),
          child: Semantics(
            identifier: 'key:$key',
            container: true,
            explicitChildNodes: true,
            child: wrapped,
          ),
        );
}
