import 'package:flutter/material.dart';
import 'package:mix/mix.dart';

import '../design/tokens.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_tokens.dart';
import 'style.dart';

/// Base colour for Figma shadows: the strong ink, or black in dark themes.
Color uiKitShadowBase(BuildContext context) =>
    Theme.of(context).brightness == Brightness.dark
    ? Colors.black
    : UiColors.of(context).strong;

/// Visual elevation tier for [UiMixSurface].
enum UiMixSurfaceVariant { card, elevated, recessed }

/// Option B `mix` Surface primitive driven by [UiColors] and [UiTokens] for
/// card, elevated, recessed, and glassmorphic containers.
final class UiMixSurface extends StatelessWidget {
  const UiMixSurface({
    super.key,
    required this.child,
    this.variant = UiMixSurfaceVariant.card,
    this.padding,
    this.radius,
    this.shadows,
    this.color,
    this.borderColor,
    this.blurSigma,
    this.style,
    this.semanticsId,
  });

  final Widget child;
  final UiMixSurfaceVariant variant;
  final EdgeInsetsGeometry? padding;
  final double? radius;
  final List<BoxShadow>? shadows;
  final Color? color;
  final Color? borderColor;
  final double? blurSigma;
  final UiStyleData? style;
  final String? semanticsId;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);

    final bg =
        color ??
        switch (variant) {
          UiMixSurfaceVariant.card => colors.surface,
          UiMixSurfaceVariant.elevated => colors.surface,
          UiMixSurfaceVariant.recessed => colors.recessed,
        };

    final effectiveRadius = radius ?? style?.radius ?? tokens.card;
    final effectiveBorder =
        borderColor ??
        (colors.isGlass ? colors.specularBorder : colors.hairline);

    Widget surface;
    if (blurSigma != null || colors.isGlass || shadows != null) {
      surface = IcAcrylicBox(
        radius: effectiveRadius,
        color: bg,
        borderColor: effectiveBorder,
        shadows: shadows ?? UiShadows.sm(colors),
        padding:
            padding ??
            EdgeInsets.symmetric(
              horizontal: style?.paddingX ?? tokens.spaceMedium,
              vertical: style?.paddingY ?? tokens.spaceMedium,
            ),
        sigmaOverride: blurSigma ?? colors.blurSigma,
        child: child,
      );
    } else {
      final px = style?.paddingX ?? tokens.spaceMedium;
      final py = style?.paddingY ?? tokens.spaceMedium;

      var boxStyle = BoxStyler()
          .color(bg)
          .borderRadius(BorderRadiusGeometryMix.circular(effectiveRadius))
          .border(
            BoxBorderMix.all(BorderSideMix.color(effectiveBorder).width(1.0)),
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

      surface = Box(
        style: boxStyle,
        child: padding != null
            ? Padding(padding: padding!, child: child)
            : child,
      );
    }

    if (semanticsId == null) return surface;
    return Semantics(identifier: semanticsId, child: surface);
  }
}
