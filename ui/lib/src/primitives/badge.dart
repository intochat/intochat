import 'package:flutter/material.dart';
import 'package:mix/mix.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

/// Semantic tone for [UiBadge].
enum UiBadgeTone { neutral, accent, success, warning, danger }

/// Mix-styled status/metadata badge with semantic tones and optional icon or dot.
final class UiBadge extends StatelessWidget {
  const UiBadge({
    super.key,
    required this.label,
    this.tone = UiBadgeTone.neutral,
    this.icon,
    this.dot = false,
    this.semanticsId,
  });

  final String label;
  final UiBadgeTone tone;
  final IconData? icon;
  final bool dot;
  final String? semanticsId;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final type = UiType.of(context);

    final (Color bg, Color fg, Color border) = switch (tone) {
      UiBadgeTone.neutral => (c.recessed, c.body, c.hairline),
      UiBadgeTone.accent => (c.accentTint, c.accent, c.accentBorder),
      UiBadgeTone.success => (c.doneBg, c.done, c.done.withValues(alpha: 0.3)),
      UiBadgeTone.warning => (
        c.workingBg,
        c.working,
        c.working.withValues(alpha: 0.3),
      ),
      UiBadgeTone.danger => (
        c.errorBg,
        c.error,
        c.error.withValues(alpha: 0.3),
      ),
    };

    final badge = Box(
      style: BoxStyler()
          .color(bg)
          .padding(
            EdgeInsetsGeometryMix.symmetric(
              horizontal: tokens.p8,
              vertical: tokens.p2 + 1,
            ),
          )
          .borderRadius(BorderRadiusGeometryMix.circular(tokens.pill))
          .border(BoxBorderMix.all(BorderSideMix.color(border).width(1.0))),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (dot) ...[
            Box(
              style: BoxStyler()
                  .width(6)
                  .height(6)
                  .color(fg)
                  .borderRadius(BorderRadiusGeometryMix.circular(tokens.pill)),
            ),
            SizedBox(width: tokens.p6),
          ] else if (icon != null) ...[
            Icon(icon, size: 12, color: fg),
            SizedBox(width: tokens.p4),
          ],
          Text(
            label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: type.meta.copyWith(
              color: fg,
              fontWeight: FontWeight.w600,
              height: 1.2,
            ),
          ),
        ],
      ),
    );

    if (semanticsId == null) return badge;
    return Semantics(
      identifier: semanticsId,
      label: label,
      child: badge,
    );
  }
}
