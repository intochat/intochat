import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_renderer.dart';

import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../components/kit/kit_toast.dart';
import '../design/cards/card_shell.dart';
import '../theme/brand_visuals.dart';

/// Semantic node tone resolved across `Badge`, `TagPill`, `Text`, and `InfoBar`.
enum UiNodeTone { neutral, accent, done, working, error }

/// The kit tone for a wire tone string (`neutral`, `done`, `working`, `error`, ...).
UiTone toneOf(String tone) => switch (nodeToneOf(tone)) {
  UiNodeTone.done => UiTone.done,
  UiNodeTone.working => UiTone.working,
  UiNodeTone.error => UiTone.error,
  UiNodeTone.neutral || UiNodeTone.accent => UiTone.neutral,
};

/// Resolves all C# semantic tone strings (`accent`, `ok`, `info`, `warn`, `needs-you`, `idle`, etc.).
UiNodeTone nodeToneOf(String tone) => switch (tone.trim().toLowerCase()) {
  'accent' ||
  'primary' ||
  'info' ||
  'needs-you' ||
  'needs_you' ||
  'needsyou' => UiNodeTone.accent,
  'done' ||
  'ok' ||
  'success' ||
  'positive' ||
  'connected' ||
  'saved' => UiNodeTone.done,
  'working' ||
  'warn' ||
  'warning' ||
  'pending' ||
  'running' ||
  'syncing' ||
  'saving' => UiNodeTone.working,
  'error' ||
  'danger' ||
  'critical' ||
  'failed' ||
  'destructive' => UiNodeTone.error,
  _ => UiNodeTone.neutral,
};

/// The card source line for a node's system name, upgraded with allowlisted
/// brand/provider/capability icons when recognized.
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
  final resolved = BrandVisuals.tryResolveCapabilityOrAppVisual(
    preferredLabel ?? name,
    iconKey: iconKey,
  );
  if (resolved != null) {
    final displayLabel =
        preferredLabel ??
        ((name.trim().isEmpty ||
                name == 'DigitalBrain' ||
                name.startsWith('I') ||
                name.contains(':'))
            ? resolved.label
            : name);
    return CardSource(displayLabel, IcBrandIcon(spec: resolved, size: 15));
  }
  if (approvalFallbackShield) {
    final shield = BrandVisuals.catalog['capability:shield']!;
    return CardSource(
      preferredLabel ?? (name.trim().isEmpty ? shield.label : name),
      IcBrandIcon(spec: shield, size: 15),
    );
  }
  return CardSource(
    preferredLabel ?? name,
    const Icon(LucideIcons.box, size: 14),
  );
}

/// A child-button callback: activates the button's key when it is enabled and the view is live.
VoidCallback? pressOf(ButtonNode? button, NodeContext ctx) =>
    button == null || !button.enabled || !ctx.interactive
    ? null
    : () => ctx.activate(button.key);

/// The children of a layout node, rendered one level deeper.
List<Widget> renderAll(List<Node> nodes, NodeContext ctx) => [
  for (final node in nodes) NodeRenderer.render(node, ctx.deeper()),
];
