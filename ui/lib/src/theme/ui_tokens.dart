// ignore_for_file: prefer_initializing_formals

import 'dart:ui';

import 'package:flutter/material.dart';

import 'ui_colors.dart';

/// Unified spatial, geometry, sizing, and motion design tokens carried on
/// [ThemeData.extensions] and derived from [UiThemeSeeds].
@immutable
class UiTokens extends ThemeExtension<UiTokens> {
  const UiTokens({
    this.p2 = 2,
    this.p4 = 4,
    this.p6 = 6,
    this.p8 = 8,
    this.p10 = 10,
    this.p12 = 12,
    this.p14 = 14,
    this.p16 = 16,
    this.p18 = 18,
    this.p20 = 20,
    this.p24 = 24,
    this.p28 = 28,
    this.p32 = 32,
    this.p40 = 40,
    this.p48 = 48,
    this.p56 = 56,
    this.spaceSmall = 8,
    this.spaceMedium = 16,
    this.spaceLarge = 24,
    this.bar = 4,
    this.xs = 6,
    this.sm = 8,
    this.button = 10,
    this.input = 12,
    this.card = 14,
    this.window = 16,
    this.panel = 20,
    this.bubble = 18,
    this.pill = 999,
    this.controlRadius = 10,
    this.surfaceRadius = 18,
    this.radiusSmall = 8,
    this.fieldRadius = 12,
    this.panelRadius = 14,
    this.modalRadius = 20,
    this.buttonHeight = 36,
    this.buttonSmallHeight = 30,
    this.iconButtonSize = 28,
    this.inputHeight = 36,
    this.titleBarHeight = 38,
    this.osBarHeight = 32,
    this.rowHeight = 32,
    this.tagPillHeight = 22,
    this.ease = const Cubic(0.2, 0.8, 0.2, 1),
    this.spring = const Cubic(0.16, 1.0, 0.3, 1.0),
    this.fast = const Duration(milliseconds: 120),
    this.base = const Duration(milliseconds: 200),
    this.slow = const Duration(milliseconds: 340),
    this.quick = const Duration(milliseconds: 120),
    this.standard = const Duration(milliseconds: 200),
    this.spinner = const Duration(milliseconds: 900),
    this.pulse = const Duration(milliseconds: 1400),
    this.skeleton = const Duration(milliseconds: 1100),
    this.typing = const Duration(milliseconds: 1000),
    this.hintDelay = const Duration(milliseconds: 350),
  });

  final double p2,
      p4,
      p6,
      p8,
      p10,
      p12,
      p14,
      p16,
      p18,
      p20,
      p24,
      p28,
      p32,
      p40,
      p48,
      p56;
  final double spaceSmall, spaceMedium, spaceLarge;
  final double bar, xs, sm, button, input, card, window, panel, bubble, pill;
  final double controlRadius,
      surfaceRadius,
      radiusSmall,
      fieldRadius,
      panelRadius,
      modalRadius;
  final double buttonHeight,
      buttonSmallHeight,
      iconButtonSize,
      inputHeight,
      titleBarHeight,
      osBarHeight,
      rowHeight,
      tagPillHeight;
  final Duration fast,
      base,
      slow,
      quick,
      standard,
      spinner,
      pulse,
      skeleton,
      typing,
      hintDelay;
  final Curve ease, spring;

  static const defaults = UiTokens();

  static UiTokens of(BuildContext context) =>
      Theme.of(context).extension<UiTokens>() ?? defaults;

  @override
  UiTokens copyWith({
    double? p2,
    double? p4,
    double? p6,
    double? p8,
    double? p10,
    double? p12,
    double? p14,
    double? p16,
    double? p18,
    double? p20,
    double? p24,
    double? p28,
    double? p32,
    double? p40,
    double? p48,
    double? p56,
    double? spaceSmall,
    double? spaceMedium,
    double? spaceLarge,
    double? bar,
    double? xs,
    double? sm,
    double? button,
    double? input,
    double? card,
    double? window,
    double? panel,
    double? bubble,
    double? pill,
    double? controlRadius,
    double? surfaceRadius,
    double? radiusSmall,
    double? fieldRadius,
    double? panelRadius,
    double? modalRadius,
    double? buttonHeight,
    double? buttonSmallHeight,
    double? iconButtonSize,
    double? inputHeight,
    double? titleBarHeight,
    double? osBarHeight,
    double? rowHeight,
    double? tagPillHeight,
    Curve? ease,
    Curve? spring,
    Duration? fast,
    Duration? base,
    Duration? slow,
    Duration? quick,
    Duration? standard,
  }) => UiTokens(
    p2: p2 ?? this.p2,
    p4: p4 ?? this.p4,
    p6: p6 ?? this.p6,
    p8: p8 ?? this.p8,
    p10: p10 ?? this.p10,
    p12: p12 ?? this.p12,
    p14: p14 ?? this.p14,
    p16: p16 ?? this.p16,
    p18: p18 ?? this.p18,
    p20: p20 ?? this.p20,
    p24: p24 ?? this.p24,
    p28: p28 ?? this.p28,
    p32: p32 ?? this.p32,
    p40: p40 ?? this.p40,
    p48: p48 ?? this.p48,
    p56: p56 ?? this.p56,
    spaceSmall: spaceSmall ?? this.spaceSmall,
    spaceMedium: spaceMedium ?? this.spaceMedium,
    spaceLarge: spaceLarge ?? this.spaceLarge,
    bar: bar ?? this.bar,
    xs: xs ?? this.xs,
    sm: sm ?? this.sm,
    button: button ?? this.button,
    input: input ?? this.input,
    card: card ?? this.card,
    window: window ?? this.window,
    panel: panel ?? this.panel,
    bubble: bubble ?? this.bubble,
    pill: pill ?? this.pill,
    controlRadius: controlRadius ?? this.controlRadius,
    surfaceRadius: surfaceRadius ?? this.surfaceRadius,
    radiusSmall: radiusSmall ?? this.radiusSmall,
    fieldRadius: fieldRadius ?? this.fieldRadius,
    panelRadius: panelRadius ?? this.panelRadius,
    modalRadius: modalRadius ?? this.modalRadius,
    buttonHeight: buttonHeight ?? this.buttonHeight,
    buttonSmallHeight: buttonSmallHeight ?? this.buttonSmallHeight,
    iconButtonSize: iconButtonSize ?? this.iconButtonSize,
    inputHeight: inputHeight ?? this.inputHeight,
    titleBarHeight: titleBarHeight ?? this.titleBarHeight,
    osBarHeight: osBarHeight ?? this.osBarHeight,
    rowHeight: rowHeight ?? this.rowHeight,
    tagPillHeight: tagPillHeight ?? this.tagPillHeight,
    ease: ease ?? this.ease,
    spring: spring ?? this.spring,
    fast: fast ?? this.fast,
    base: base ?? this.base,
    slow: slow ?? this.slow,
    quick: quick ?? this.quick,
    standard: standard ?? this.standard,
  );

  @override
  UiTokens lerp(UiTokens? other, double t) {
    if (other == null) return this;
    if (t == 0.0) return this;
    if (t == 1.0) return other;
    return UiTokens(
      p2: lerpDouble(p2, other.p2, t)!,
      p4: lerpDouble(p4, other.p4, t)!,
      p6: lerpDouble(p6, other.p6, t)!,
      p8: lerpDouble(p8, other.p8, t)!,
      p10: lerpDouble(p10, other.p10, t)!,
      p12: lerpDouble(p12, other.p12, t)!,
      p14: lerpDouble(p14, other.p14, t)!,
      p16: lerpDouble(p16, other.p16, t)!,
      p18: lerpDouble(p18, other.p18, t)!,
      p20: lerpDouble(p20, other.p20, t)!,
      p24: lerpDouble(p24, other.p24, t)!,
      p28: lerpDouble(p28, other.p28, t)!,
      p32: lerpDouble(p32, other.p32, t)!,
      p40: lerpDouble(p40, other.p40, t)!,
      p48: lerpDouble(p48, other.p48, t)!,
      p56: lerpDouble(p56, other.p56, t)!,
      spaceSmall: lerpDouble(spaceSmall, other.spaceSmall, t)!,
      spaceMedium: lerpDouble(spaceMedium, other.spaceMedium, t)!,
      spaceLarge: lerpDouble(spaceLarge, other.spaceLarge, t)!,
      bar: lerpDouble(bar, other.bar, t)!,
      xs: lerpDouble(xs, other.xs, t)!,
      sm: lerpDouble(sm, other.sm, t)!,
      button: lerpDouble(button, other.button, t)!,
      input: lerpDouble(input, other.input, t)!,
      card: lerpDouble(card, other.card, t)!,
      window: lerpDouble(window, other.window, t)!,
      panel: lerpDouble(panel, other.panel, t)!,
      bubble: lerpDouble(bubble, other.bubble, t)!,
      pill: lerpDouble(pill, other.pill, t)!,
      controlRadius: lerpDouble(controlRadius, other.controlRadius, t)!,
      surfaceRadius: lerpDouble(surfaceRadius, other.surfaceRadius, t)!,
      radiusSmall: lerpDouble(radiusSmall, other.radiusSmall, t)!,
      fieldRadius: lerpDouble(fieldRadius, other.fieldRadius, t)!,
      panelRadius: lerpDouble(panelRadius, other.panelRadius, t)!,
      modalRadius: lerpDouble(modalRadius, other.modalRadius, t)!,
      buttonHeight: lerpDouble(buttonHeight, other.buttonHeight, t)!,
      buttonSmallHeight: lerpDouble(
        buttonSmallHeight,
        other.buttonSmallHeight,
        t,
      )!,
      iconButtonSize: lerpDouble(iconButtonSize, other.iconButtonSize, t)!,
      inputHeight: lerpDouble(inputHeight, other.inputHeight, t)!,
      titleBarHeight: lerpDouble(titleBarHeight, other.titleBarHeight, t)!,
      osBarHeight: lerpDouble(osBarHeight, other.osBarHeight, t)!,
      rowHeight: lerpDouble(rowHeight, other.rowHeight, t)!,
      tagPillHeight: lerpDouble(tagPillHeight, other.tagPillHeight, t)!,
      ease: t < 0.5 ? ease : other.ease,
      spring: t < 0.5 ? spring : other.spring,
      fast: Duration(
        microseconds: lerpDouble(
          fast.inMicroseconds,
          other.fast.inMicroseconds,
          t,
        )!.round(),
      ),
      base: Duration(
        microseconds: lerpDouble(
          base.inMicroseconds,
          other.base.inMicroseconds,
          t,
        )!.round(),
      ),
      slow: Duration(
        microseconds: lerpDouble(
          slow.inMicroseconds,
          other.slow.inMicroseconds,
          t,
        )!.round(),
      ),
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

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is UiTokens &&
          other.p2 == p2 &&
          other.p4 == p4 &&
          other.p6 == p6 &&
          other.p8 == p8 &&
          other.p10 == p10 &&
          other.p12 == p12 &&
          other.p14 == p14 &&
          other.p16 == p16 &&
          other.p18 == p18 &&
          other.p20 == p20 &&
          other.p24 == p24 &&
          other.p28 == p28 &&
          other.p32 == p32 &&
          other.p40 == p40 &&
          other.p48 == p48 &&
          other.p56 == p56 &&
          other.spaceSmall == spaceSmall &&
          other.spaceMedium == spaceMedium &&
          other.spaceLarge == spaceLarge &&
          other.bar == bar &&
          other.xs == xs &&
          other.sm == sm &&
          other.button == button &&
          other.input == input &&
          other.card == card &&
          other.window == window &&
          other.panel == panel &&
          other.bubble == bubble &&
          other.pill == pill &&
          other.controlRadius == controlRadius &&
          other.surfaceRadius == surfaceRadius &&
          other.radiusSmall == radiusSmall &&
          other.fieldRadius == fieldRadius &&
          other.panelRadius == panelRadius &&
          other.modalRadius == modalRadius &&
          other.buttonHeight == buttonHeight &&
          other.buttonSmallHeight == buttonSmallHeight &&
          other.iconButtonSize == iconButtonSize &&
          other.inputHeight == inputHeight &&
          other.titleBarHeight == titleBarHeight &&
          other.osBarHeight == osBarHeight &&
          other.rowHeight == rowHeight &&
          other.tagPillHeight == tagPillHeight &&
          other.ease == ease &&
          other.spring == spring &&
          other.fast == fast &&
          other.base == base &&
          other.slow == slow &&
          other.quick == quick &&
          other.standard == standard &&
          other.spinner == spinner &&
          other.pulse == pulse &&
          other.skeleton == skeleton &&
          other.typing == typing &&
          other.hintDelay == hintDelay;

  @override
  int get hashCode => Object.hashAll([
    p2,
    p4,
    p6,
    p8,
    p10,
    p12,
    p14,
    p16,
    p18,
    p20,
    p24,
    p28,
    p32,
    p40,
    p48,
    p56,
    spaceSmall,
    spaceMedium,
    spaceLarge,
    bar,
    xs,
    sm,
    button,
    input,
    card,
    window,
    panel,
    bubble,
    pill,
    controlRadius,
    surfaceRadius,
    radiusSmall,
    fieldRadius,
    panelRadius,
    modalRadius,
    buttonHeight,
    buttonSmallHeight,
    iconButtonSize,
    inputHeight,
    titleBarHeight,
    osBarHeight,
    rowHeight,
    tagPillHeight,
    ease,
    spring,
    fast,
    base,
    slow,
    quick,
    standard,
    spinner,
    pulse,
    skeleton,
    typing,
    hintDelay,
  ]);
}

/// 3D shader/render aesthetic for `BrainScene3D`.
enum IcBrainShaderMode {
  volumetricGlass('volumetric-glass', 'Volumetric Glass'),
  wireframeHud('wireframe-hud', 'Wireframe HUD'),
  architecturalBlueprint('architectural-blueprint', 'Architectural Blueprint'),
  starMapConstellation('star-map-constellation', 'Star-Map Constellation');

  const IcBrainShaderMode(this.id, this.label);
  final String id;
  final String label;
}

/// Shell layout & positioning preset paired with a visual identity.
enum BarLayout {
  floatingGlass('floating-glass', 'Floating Glass Dock'),
  splitTop('split-top', 'Top Bar + Floating Dock'),
  classicBottom('classic-bottom', 'Full-Width Bottom Bar'),
  compactPill('compact-pill', 'Compact Pill Bar');

  const BarLayout(this.id, this.label);
  final String id;
  final String label;

  static BarLayout fromId(String? id) {
    for (final b in BarLayout.values) {
      if (b.id == id) return b;
    }
    return BarLayout.floatingGlass;
  }
}

/// Visual identity preset: all 7 OS UI Identities with wire-compatible string
/// IDs matching C# `DesktopAppState.StylePreset` / settings preferences.
enum IcStylePreset {
  fluentAcrylic(
    'fluent-acrylic',
    'Fluent Acrylic',
    'Warm Mica & WinUI 3 Acrylic glass with specular borders and spring motion.',
    IcBrainShaderMode.volumetricGlass,
    BarLayout.floatingGlass,
  ),
  spatialGlass(
    'spatial-glass',
    'Spatial Glass',
    'Deep translucent VisionOS frosted glass floating over the live 3D brain.',
    IcBrainShaderMode.volumetricGlass,
    BarLayout.splitTop,
  ),
  cyberSynaptic(
    'cyber-synaptic',
    'Cyber Synaptic',
    'Bioluminescent neural void with emerald & cyan synaptic signal glow.',
    IcBrainShaderMode.wireframeHud,
    BarLayout.floatingGlass,
  ),
  warmPaper(
    'warm-paper',
    'Warm Ink & Paper',
    'High-contrast tactile warm paper and ink with crisp 1px hairlines.',
    IcBrainShaderMode.architecturalBlueprint,
    BarLayout.classicBottom,
  ),
  fluentWarmMica(
    'fluent-warm-mica',
    'Fluent Warm Mica',
    'WinUI 3 Warm Espresso & Amber Acrylic with soft specular warmth.',
    IcBrainShaderMode.volumetricGlass,
    BarLayout.floatingGlass,
  ),
  linearCarbon(
    'linear-carbon',
    'Linear Carbon',
    'Hyper-fluid dark carbon with top-lit 1px borders and electric indigo accent.',
    IcBrainShaderMode.starMapConstellation,
    BarLayout.compactPill,
  ),
  nordicTactical(
    'nordic-tactical',
    'Nordic Tactical',
    '2D/3D architectural blueprint with slate & signal cyan telemetry.',
    IcBrainShaderMode.architecturalBlueprint,
    BarLayout.splitTop,
  );

  const IcStylePreset(
    this.id,
    this.label,
    this.subtitle,
    this.defaultShaderMode,
    this.defaultBarLayout,
  );

  final String id;
  final String label;
  final String subtitle;
  final IcBrainShaderMode defaultShaderMode;
  final BarLayout defaultBarLayout;

  String get summary => subtitle;
  String get description => subtitle;

  static IcStylePreset fromId(String? id) {
    if (id == 'intochat') return IcStylePreset.warmPaper;
    for (final p in IcStylePreset.values) {
      if (p.id == id) return p;
    }
    return IcStylePreset.warmPaper;
  }
}

/// Desktop background tones (Settings > Desktop & Dock).
enum DesktopTone { cream, sand, ink }

extension DesktopToneColor on DesktopTone {
  String get label => switch (this) {
    DesktopTone.cream => 'Cream',
    DesktopTone.sand => 'Sand',
    DesktopTone.ink => 'Ink',
  };

  Color color(UiColors c) => switch (c.preset) {
    IcStylePreset.fluentAcrylic =>
      c.isDark ? const Color(0xFF0A0D14) : const Color(0xFFEDEBE4),
    IcStylePreset.spatialGlass =>
      c.isDark ? const Color(0xFF060913) : const Color(0xFFE2E8F0),
    IcStylePreset.cyberSynaptic => const Color(0xFF04080B),
    IcStylePreset.fluentWarmMica =>
      c.isDark ? const Color(0xFF120F0C) : const Color(0xFFF3EFE8),
    IcStylePreset.linearCarbon => const Color(0xFF08090C),
    IcStylePreset.nordicTactical =>
      c.isDark ? const Color(0xFF0B0F14) : const Color(0xFFE2E8F0),
    IcStylePreset.warmPaper => switch (this) {
      DesktopTone.cream =>
        c.isDark ? const Color(0xFF1D1914) : const Color(0xFFF2EFE9),
      DesktopTone.sand =>
        c.isDark ? const Color(0xFF231E17) : const Color(0xFFE6DFD2),
      DesktopTone.ink =>
        c.isDark ? const Color(0xFF0D0B08) : const Color(0xFF2A251E),
    },
  };
}
