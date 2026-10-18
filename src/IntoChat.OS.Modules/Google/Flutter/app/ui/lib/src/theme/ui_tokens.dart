import 'dart:ui' show lerpDouble;

import 'package:flutter/material.dart';

@immutable
class UiTokens extends ThemeExtension<UiTokens> {
  const UiTokens({
    this.spaceSmall = 8,
    this.spaceMedium = 16,
    this.spaceLarge = 24,
    this.controlRadius = 10,
    this.surfaceRadius = 18,
    this.quick = const Duration(milliseconds: 120),
    this.standard = const Duration(milliseconds: 200),
  });
  final double spaceSmall,
      spaceMedium,
      spaceLarge,
      controlRadius,
      surfaceRadius;
  final Duration quick, standard;
  static UiTokens of(BuildContext context) =>
      Theme.of(context).extension<UiTokens>() ?? const UiTokens();
  @override
  UiTokens copyWith({
    double? spaceSmall,
    double? spaceMedium,
    double? spaceLarge,
    double? controlRadius,
    double? surfaceRadius,
    Duration? quick,
    Duration? standard,
  }) => UiTokens(
    spaceSmall: spaceSmall ?? this.spaceSmall,
    spaceMedium: spaceMedium ?? this.spaceMedium,
    spaceLarge: spaceLarge ?? this.spaceLarge,
    controlRadius: controlRadius ?? this.controlRadius,
    surfaceRadius: surfaceRadius ?? this.surfaceRadius,
    quick: quick ?? this.quick,
    standard: standard ?? this.standard,
  );
  @override
  UiTokens lerp(UiTokens? other, double t) {
    if (other == null) return this;
    return UiTokens(
      spaceSmall: lerpDouble(spaceSmall, other.spaceSmall, t)!,
      spaceMedium: lerpDouble(spaceMedium, other.spaceMedium, t)!,
      spaceLarge: lerpDouble(spaceLarge, other.spaceLarge, t)!,
      controlRadius: lerpDouble(controlRadius, other.controlRadius, t)!,
      surfaceRadius: lerpDouble(surfaceRadius, other.surfaceRadius, t)!,
      quick: Duration(
        microseconds: lerpDouble(
          quick.inMicroseconds,
          other.quick.inMicroseconds,
          t,
        )!.round(),
      ),
      standard: Duration(
        microseconds: lerpDouble(
          standard.inMicroseconds,
          other.standard.inMicroseconds,
          t,
        )!.round(),
      ),
    );
  }
}
