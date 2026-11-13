import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/kit/ui_kit_surface.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_shadows.dart';
import 'node_common.dart';

Widget renderSurface(SurfaceNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final theme = Theme.of(context);
    final c = UiColors.of(context);
    final kind = node.surfaceKind.toLowerCase();
    final (color, borderColor, shadows, blurSigma) = switch (kind) {
      'recessed' => (c.recessed, c.hairline, const <BoxShadow>[], 0.0),
      'elevated' => (
        c.elevated,
        c.isGlass ? c.specularBorder : c.hairline,
        UiShadows.md(c),
        c.blurSigma,
      ),
      'acrylic' => (
        c.chrome,
        c.chromeBorder,
        UiShadows.sm(c),
        c.blurSigma > 0 ? c.blurSigma : 20.0,
      ),
      _ => (
        c.surface,
        c.isGlass ? c.specularBorder : c.hairline,
        UiShadows.sm(c),
        0.0,
      ),
    };
    return UiKitSurface(
      padding: EdgeInsets.all(node.padding.clamp(0, 128)),
      color: color,
      borderColor: borderColor,
      shadows: shadows,
      blurSigma: blurSigma,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 12,
        children: [
          if (node.title case final title? when title.isNotEmpty)
            Text(title, style: theme.textTheme.titleMedium),
          if (node.subtitle case final subtitle? when subtitle.isNotEmpty)
            Text(subtitle, style: theme.textTheme.bodySmall),
          ...renderAll(node.children, ctx),
        ],
      ),
    );
  },
);
