import 'package:flutter/material.dart';
import 'package:mix/mix.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_tokens.dart';
import 'ui_mix_style.dart';

/// Visual elevation tier for [UiMixSurface].
enum UiMixSurfaceVariant { card, elevated, recessed }

/// Option B `mix` Surface primitive driven by [UiColors] and [UiTokens] for
/// card, elevated, and recessed containers.
final class UiMixSurface extends StatelessWidget {
  const UiMixSurface({
    super.key,
    required this.child,
    this.variant = UiMixSurfaceVariant.card,
    this.padding,
    this.radius,
    this.style,
    this.semanticsId,
  });

  final Widget child;
  final UiMixSurfaceVariant variant;
  final EdgeInsetsGeometry? padding;
  final double? radius;
  final UiStyleData? style;
  final String? semanticsId;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);

    final bg = switch (variant) {
      UiMixSurfaceVariant.card => colors.surface,
      UiMixSurfaceVariant.elevated => colors.surface,
      UiMixSurfaceVariant.recessed => colors.recessed,
    };

    final effectiveRadius = radius ?? style?.radius ?? tokens.card;
    final px = style?.paddingX ?? tokens.spaceMedium;
    final py = style?.paddingY ?? tokens.spaceMedium;

    var boxStyle = BoxStyler()
        .color(bg)
        .borderRadius(BorderRadiusGeometryMix.circular(effectiveRadius))
        .border(
          BoxBorderMix.all(BorderSideMix.color(colors.hairline).width(1.0)),
        );

    if (padding == null) {
      boxStyle = boxStyle.padding(
        EdgeInsetsGeometryMix.symmetric(horizontal: px, vertical: py),
      );
    }

    if (variant == UiMixSurfaceVariant.elevated) {
      boxStyle = boxStyle.shadow(
        BoxShadowMix(
          color: colors.hairline.withValues(alpha: 0.35),
          blurRadius: 12,
          offset: const Offset(0, 4),
        ),
      );
    }

    final surface = Box(
      style: boxStyle,
      child: padding != null ? Padding(padding: padding!, child: child) : child,
    );

    if (semanticsId == null) return surface;
    return Semantics(identifier: semanticsId, child: surface);
  }
}
