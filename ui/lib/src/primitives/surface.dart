import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:mix/mix.dart';

import '../theme/mix_tokens.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_tokens.dart';
import 'style.dart';

/// Base colour for Figma shadows: the strong ink, or black in dark themes.
Color uiKitShadowBase(BuildContext context) =>
    Theme.of(context).brightness == Brightness.dark
    ? Colors.black
    : UiColors.of(context).strong;

/// Reusable WinUI 3 / visionOS Acrylic Frosted Glass surface driven directly
/// by [UiColors] and [UiTokens].
final class UiAcrylicSurface extends StatelessWidget {
  const UiAcrylicSurface({
    super.key,
    required this.child,
    this.borderRadius,
    this.radius,
    this.padding,
    this.strong = false,
    this.blurSigma,
    this.sigmaOverride,
    this.fillColor,
    this.color,
    this.borderColor,
    this.shadows,
    this.specularTop = false,
  });

  final Widget child;
  final BorderRadius? borderRadius;
  final double? radius;
  final EdgeInsetsGeometry? padding;
  final bool strong;
  final double? blurSigma;
  final double? sigmaOverride;
  final Color? fillColor;
  final Color? color;
  final Color? borderColor;
  final List<BoxShadow>? shadows;
  final bool specularTop;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final cardRadius = UiMixTokens.resolveOr(
      context,
      UiMixTokens.cardRadius,
      Radius.circular(tokens.card),
    ).x;
    final effectiveRadius =
        borderRadius ?? BorderRadius.circular(radius ?? cardRadius);
    final sigma = sigmaOverride ?? blurSigma ?? c.blurSigma;
    final fill =
        color ?? fillColor ?? (strong ? c.surfaceGlassStrong : c.surface);
    final border = borderColor ?? (c.isGlass ? c.specularBorder : c.hairline);

    final box = Container(
      padding: padding,
      decoration: BoxDecoration(
        color: fill,
        borderRadius: effectiveRadius,
        border: Border.all(color: border),
        boxShadow: shadows,
      ),
      child: child,
    );

    if (sigma <= 0) return box;

    return ClipRRect(
      borderRadius: effectiveRadius,
      child: BackdropFilter(
        filter: ImageFilter.blur(sigmaX: sigma, sigmaY: sigma),
        child: box,
      ),
    );
  }
}

/// Visual elevation tier for [UiMixSurface].
enum UiMixSurfaceVariant { card, elevated, recessed }

/// Option B `mix` Surface primitive driven by [UiMixTokens], [UiColors], and
/// [UiTokens] for card, elevated, recessed, and glassmorphic containers.
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

    final surfaceColor = UiMixTokens.resolveOr(
      context,
      UiMixTokens.surface,
      colors.surface,
    );
    final recessedColor = UiMixTokens.resolveOr(
      context,
      UiMixTokens.recessed,
      colors.recessed,
    );
    final spaceMedium = UiMixTokens.resolveOr(
      context,
      UiMixTokens.spaceMedium,
      tokens.spaceMedium,
    );
    final cardRadius = UiMixTokens.resolveOr(
      context,
      UiMixTokens.cardRadius,
      Radius.circular(tokens.card),
    ).x;

    final bg =
        color ??
        (style != null && style!.tone != 'default'
            ? UiStyleMapper.resolveToneBackground(colors, style!.tone)
            : switch (variant) {
                UiMixSurfaceVariant.card => surfaceColor,
                UiMixSurfaceVariant.elevated => surfaceColor,
                UiMixSurfaceVariant.recessed => recessedColor,
              });

    final styleShadows =
        shadows ??
        (style != null && style!.elevation != 'flat'
            ? UiStyleMapper.elevationShadows(colors, style!.elevation)
            : null);

    final effectiveRadius = radius ?? style?.radius ?? cardRadius;
    final Color effectiveBorder =
        borderColor ??
        (colors.isGlass
            ? colors.specularBorder
            : UiMixTokens.resolveOr<Color>(
                context,
                UiMixTokens.hairline,
                colors.hairline,
              ));

    Widget surface;
    if (blurSigma != null ||
        colors.isGlass ||
        (styleShadows != null && styleShadows.isNotEmpty)) {
      surface = UiAcrylicSurface(
        radius: effectiveRadius,
        color: bg,
        borderColor: effectiveBorder,
        shadows: styleShadows ?? UiShadows.sm(colors),
        padding:
            padding ??
            EdgeInsets.symmetric(
              horizontal: style?.paddingX ?? spaceMedium,
              vertical: style?.paddingY ?? spaceMedium,
            ),
        sigmaOverride: blurSigma ?? colors.blurSigma,
        child: child,
      );
    } else {
      final px = style?.paddingX ?? spaceMedium;
      final py = style?.paddingY ?? spaceMedium;

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
            color: effectiveBorder.withValues(alpha: 0.35),
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
