import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:mix/mix.dart';

import 'ui_colors.dart';
import 'ui_theme.dart';
import 'ui_tokens.dart';

/// Spatial density scale for spacing and control heights.
enum UiDensity {
  compact(0.82),
  comfortable(1.0),
  spacious(1.18);

  const UiDensity(this.factor);

  /// Multiplier applied to base spacing and control height tokens.
  final double factor;
}

/// Corner radius scale for controls, cards, and surfaces.
enum UiRadiusScale {
  sharp(0.4),
  fluent(1.0),
  pill(1.8);

  const UiRadiusScale(this.factor);

  /// Multiplier applied to base corner radius tokens.
  final double factor;
}

/// Global motion feel for animated transitions.
enum UiMotionMode {
  snappy(0.72),
  spring(1.0),
  reduced(0.0);

  const UiMotionMode(this.durationFactor);

  /// Multiplier applied to animation durations (`0.0` disables motion).
  final double durationFactor;
}

/// Computes the WCAG 2.1 contrast ratio between two opaque colours (`1.0`–`21.0`).
double contrastRatio(Color a, Color b) {
  final l1 = a.computeLuminance();
  final l2 = b.computeLuminance();
  final lighter = math.max(l1, l2);
  final darker = math.min(l1, l2);
  return (lighter + 0.05) / (darker + 0.05);
}

/// Returns the highest-contrast readable foreground (`white` or warm dark ink)
/// for a given [accent] colour.
Color deriveOnAccent(Color accent) {
  const lightInk = Color(0xFFFFFFFF);
  const darkInk = Color(0xFF17140F);
  return contrastRatio(accent, lightInk) >= contrastRatio(accent, darkInk)
      ? lightInk
      : darkInk;
}

/// Returns a concise WCAG 2.1 compliance badge (`AAA`, `AA`, `AA Large`, `Fail`).
String wcagContrastGrade(double ratio) {
  if (ratio >= 7.0) return 'AAA';
  if (ratio >= 4.5) return 'AA';
  if (ratio >= 3.0) return 'AA Large';
  return 'Fail';
}

/// Canonical `mix` 2.5.0 design tokens populated by [UiThemeSeeds.toMixTokens],
/// provided via [MixScope], and consumed lazily via callable references
/// (`UiMixTokens.accent()`, `UiMixTokens.spaceMedium()`,
/// `UiMixTokens.controlRadius()`, etc.) in [UiStyleMapper] and Mix primitives.
///
/// Backed by [ContextToken] so every token reads [MixScope] overrides first and
/// falls back cleanly to [UiColors.of] / [UiTokens.of] when rendered outside a
/// [MixScope] (for example, isolated widget tests).
abstract final class UiMixTokens {
  static Color _accent(BuildContext context) => UiColors.of(context).accent;
  static Color _accentHover(BuildContext context) =>
      UiColors.of(context).accentHover;
  static Color _accentTint(BuildContext context) =>
      UiColors.of(context).accentTint;
  static Color _accentCopy(BuildContext context) =>
      UiColors.of(context).accentCopy;
  static Color _onAccent(BuildContext context) => UiColors.of(context).onAccent;
  static Color _focus(BuildContext context) => UiColors.of(context).focus;

  static Color _page(BuildContext context) => UiColors.of(context).page;
  static Color _elevated(BuildContext context) => UiColors.of(context).elevated;
  static Color _surface(BuildContext context) => UiColors.of(context).surface;
  static Color _recessed(BuildContext context) => UiColors.of(context).recessed;

  static Color _strong(BuildContext context) => UiColors.of(context).strong;
  static Color _body(BuildContext context) => UiColors.of(context).body;
  static Color _hairline(BuildContext context) => UiColors.of(context).hairline;

  static Color _done(BuildContext context) => UiColors.of(context).done;
  static Color _working(BuildContext context) => UiColors.of(context).working;
  static Color _error(BuildContext context) => UiColors.of(context).error;

  static double _spaceSmall(BuildContext context) =>
      UiTokens.of(context).spaceSmall;
  static double _spaceMedium(BuildContext context) =>
      UiTokens.of(context).spaceMedium;
  static double _spaceLarge(BuildContext context) =>
      UiTokens.of(context).spaceLarge;

  static Radius _controlRadius(BuildContext context) =>
      Radius.circular(UiTokens.of(context).controlRadius);
  static Radius _cardRadius(BuildContext context) =>
      Radius.circular(UiTokens.of(context).card);
  static Radius _surfaceRadius(BuildContext context) =>
      Radius.circular(UiTokens.of(context).surfaceRadius);
  static Radius _pillRadius(BuildContext context) =>
      Radius.circular(UiTokens.of(context).pill);

  static Duration _fastDuration(BuildContext context) =>
      UiTokens.of(context).fast;
  static Duration _baseDuration(BuildContext context) =>
      UiTokens.of(context).base;
  static Duration _slowDuration(BuildContext context) =>
      UiTokens.of(context).slow;

  // Colour tokens
  static const accent = ContextToken<Color>(_accent);
  static const accentHover = ContextToken<Color>(_accentHover);
  static const accentTint = ContextToken<Color>(_accentTint);
  static const accentCopy = ContextToken<Color>(_accentCopy);
  static const onAccent = ContextToken<Color>(_onAccent);
  static const focus = ContextToken<Color>(_focus);

  static const page = ContextToken<Color>(_page);
  static const elevated = ContextToken<Color>(_elevated);
  static const surface = ContextToken<Color>(_surface);
  static const recessed = ContextToken<Color>(_recessed);

  static const strong = ContextToken<Color>(_strong);
  static const body = ContextToken<Color>(_body);
  static const hairline = ContextToken<Color>(_hairline);

  static const done = ContextToken<Color>(_done);
  static const working = ContextToken<Color>(_working);
  static const error = ContextToken<Color>(_error);

  // Spacing tokens
  static const spaceSmall = ContextToken<double>(_spaceSmall);
  static const spaceMedium = ContextToken<double>(_spaceMedium);
  static const spaceLarge = ContextToken<double>(_spaceLarge);

  // Radius tokens
  static const controlRadius = ContextToken<Radius>(_controlRadius);
  static const cardRadius = ContextToken<Radius>(_cardRadius);
  static const surfaceRadius = ContextToken<Radius>(_surfaceRadius);
  static const pillRadius = ContextToken<Radius>(_pillRadius);

  // Motion duration tokens
  static const fastDuration = ContextToken<Duration>(_fastDuration);
  static const baseDuration = ContextToken<Duration>(_baseDuration);
  static const slowDuration = ContextToken<Duration>(_slowDuration);

  /// Resolves [token] from the enclosing [MixScope], falling back cleanly to
  /// [fallback] when rendered outside a [MixScope] (e.g., isolated widget tests).
  static T resolveOr<T>(BuildContext context, MixToken<T> token, T fallback) {
    final scope = MixScope.maybeOf(context, 'tokens');
    if (scope == null || scope.tokens?[token] == null) return fallback;
    return scope.getToken(token, context);
  }
}

/// Unified seed-to-ramp theme definition driving every OS identity and live
/// morphing control from one immutable value.
///
/// A single [UiThemeSeeds] drives:
/// - [UiColors] (`UiColors.of(context)`) across all 7 [IcStylePreset] identities
/// - [UiTokens] (`UiTokens.of(context)`)
/// - [UiTypeSet] (`UiType.of(context)`)
/// - [MixScope] tokens ([UiMixTokens])
/// - Material 3 [ThemeData]
@immutable
final class UiThemeSeeds {
  const UiThemeSeeds({
    required this.accent,
    this.preset = IcStylePreset.warmPaper,
    this.brightness = Brightness.light,
    this.density = UiDensity.comfortable,
    this.radiusScale = UiRadiusScale.fluent,
    this.motion = UiMotionMode.spring,
  });

  /// Builds canonical seeds for one of the 7 OS [IcStylePreset] identities.
  factory UiThemeSeeds.forPreset(
    IcStylePreset preset, {
    Brightness brightness = Brightness.dark,
    Color? accent,
    UiDensity density = UiDensity.comfortable,
    UiRadiusScale radiusScale = UiRadiusScale.fluent,
    UiMotionMode motion = UiMotionMode.spring,
  }) {
    final base = UiColors.forPreset(
      preset,
      dark: brightness == Brightness.dark,
    );
    return UiThemeSeeds(
      preset: preset,
      accent: accent ?? base.accent,
      brightness: brightness,
      density: density,
      radiusScale: radiusScale,
      motion: motion,
    );
  }

  /// Builds canonical seeds from a persisted theme string (either one of the 7
  /// OS [IcStylePreset] wire IDs or a legacy v1 preference name mapped at
  /// ingress).
  factory UiThemeSeeds.fromStoredTheme(
    String? stored, {
    required Brightness brightness,
    UiDensity density = UiDensity.comfortable,
    UiRadiusScale radiusScale = UiRadiusScale.fluent,
    UiMotionMode motion = UiMotionMode.spring,
  }) {
    final base = UiColors.forStoredTheme(stored, brightness: brightness);
    return UiThemeSeeds(
      preset: base.preset,
      accent: base.accent,
      brightness: base.brightness,
      density: density,
      radiusScale: radiusScale,
      motion: motion,
    );
  }

  final IcStylePreset preset;
  final Color accent;
  final Brightness brightness;
  final UiDensity density;
  final UiRadiusScale radiusScale;
  final UiMotionMode motion;

  /// Warm Paper Light (IntoChat default light theme).
  static const warmPaper = UiThemeSeeds(
    preset: IcStylePreset.warmPaper,
    accent: Color(0xFF1F5EFF),
    brightness: Brightness.light,
    density: UiDensity.comfortable,
    radiusScale: UiRadiusScale.fluent,
    motion: UiMotionMode.spring,
  );

  /// Warm Dark (IntoChat default dark theme).
  static const warmDark = UiThemeSeeds(
    preset: IcStylePreset.warmPaper,
    accent: Color(0xFF4B7DFF),
    brightness: Brightness.dark,
    density: UiDensity.comfortable,
    radiusScale: UiRadiusScale.fluent,
    motion: UiMotionMode.spring,
  );

  /// Fluent Blue (Windows Fluent Acrylic in light mode).
  static const fluentBlue = UiThemeSeeds(
    preset: IcStylePreset.fluentAcrylic,
    accent: Color(0xFF0078D4),
    brightness: Brightness.light,
    density: UiDensity.comfortable,
    radiusScale: UiRadiusScale.fluent,
    motion: UiMotionMode.snappy,
  );

  /// Emerald Studio (Cyber Synaptic dark studio with emerald accent).
  static const emeraldStudio = UiThemeSeeds(
    preset: IcStylePreset.cyberSynaptic,
    accent: Color(0xFF10B981),
    brightness: Brightness.dark,
    density: UiDensity.comfortable,
    radiusScale: UiRadiusScale.fluent,
    motion: UiMotionMode.spring,
  );

  /// Compact Pro (Linear Carbon high-density dark workspace with crisp corners).
  static const compactPro = UiThemeSeeds(
    preset: IcStylePreset.linearCarbon,
    accent: Color(0xFF6366F1),
    brightness: Brightness.dark,
    density: UiDensity.compact,
    radiusScale: UiRadiusScale.sharp,
    motion: UiMotionMode.snappy,
  );

  /// Named presets for theme pickers and live studios.
  static const List<({String label, UiThemeSeeds seeds})> presets = [
    (label: 'Warm Paper', seeds: warmPaper),
    (label: 'Warm Dark', seeds: warmDark),
    (label: 'Fluent Blue', seeds: fluentBlue),
    (label: 'Emerald', seeds: emeraldStudio),
    (label: 'Compact Pro', seeds: compactPro),
  ];

  UiColors get _baseColors =>
      UiColors.forPreset(preset, dark: brightness == Brightness.dark);

  /// Guaranteed readable foreground colour on top of [accent].
  Color get onAccent {
    final base = _baseColors;
    return accent.toARGB32() == base.accent.toARGB32()
        ? base.onAccent
        : deriveOnAccent(accent);
  }

  /// WCAG 2.1 contrast ratio between [accent] and [onAccent].
  double get accentContrastRatio => contrastRatio(accent, onAccent);

  /// WCAG 2.1 compliance badge (`AAA`, `AA`, `AA Large`, `Fail`).
  String get accentContrastGrade => wcagContrastGrade(accentContrastRatio);

  UiThemeSeeds copyWith({
    IcStylePreset? preset,
    Color? accent,
    Brightness? brightness,
    UiDensity? density,
    UiRadiusScale? radiusScale,
    UiMotionMode? motion,
  }) => UiThemeSeeds(
    preset: preset ?? this.preset,
    accent: accent ?? this.accent,
    brightness: brightness ?? this.brightness,
    density: density ?? this.density,
    radiusScale: radiusScale ?? this.radiusScale,
    motion: motion ?? this.motion,
  );

  /// Derives a full [UiColors] ramp from [preset], [brightness], and [accent].
  ///
  /// When [accent] matches the preset's canonical accent, returns the exact
  /// hand-tuned [UiColors] palette verbatim (zero rounding drift). Otherwise
  /// morphs the accent ramp on top of the active preset's surfaces and glass.
  UiColors toUiColors() {
    final base = _baseColors;
    if (accent.toARGB32() == base.accent.toARGB32()) {
      return base;
    }

    final isDark = brightness == Brightness.dark;
    final hsl = HSLColor.fromColor(accent);
    final hoverLightness = isDark
        ? (hsl.lightness + 0.08).clamp(0.0, 1.0)
        : (hsl.lightness - 0.09).clamp(0.0, 1.0);
    final accentHover = hsl.withLightness(hoverLightness).toColor();
    final accentTint = isDark
        ? accent.withValues(alpha: 0.18)
        : Color.lerp(Colors.white, accent, 0.12)!;
    final accentBorder = accent.withValues(alpha: isDark ? 0.45 : 0.35);
    final derivedOnAccent = deriveOnAccent(accent);

    return base.copyWith(
      accent: accent,
      accentHover: accentHover,
      accentTint: accentTint,
      accentBorder: accentBorder,
      onAccent: derivedOnAccent,
      accentCopy: isDark ? Color.lerp(Colors.white, accent, 0.35)! : accent,
      accentCopyHover: accentHover,
      focus: accentBorder,
      ambientGlow: accent,
      needsYou: accent,
      needsYouBg: accentTint,
    );
  }

  /// Derives scaled [UiTokens] from [density], [radiusScale], and [motion].
  UiTokens toUiTokens() {
    if (density == UiDensity.comfortable &&
        radiusScale == UiRadiusScale.fluent &&
        motion == UiMotionMode.spring) {
      return UiTokens.defaults;
    }
    const base = UiTokens.defaults;
    final d = density.factor;
    final r = radiusScale.factor;
    final m = motion.durationFactor;

    double sp(double value) => (value * d).roundToDouble();
    double rad(double value) => (value * r).clamp(2.0, 28.0).roundToDouble();
    Duration dur(Duration value) => m == 0.0
        ? Duration.zero
        : Duration(milliseconds: (value.inMilliseconds * m).round());

    return base.copyWith(
      p4: sp(base.p4),
      p6: sp(base.p6),
      p8: sp(base.p8),
      p10: sp(base.p10),
      p12: sp(base.p12),
      p14: sp(base.p14),
      p16: sp(base.p16),
      p20: sp(base.p20),
      p24: sp(base.p24),
      spaceSmall: sp(base.spaceSmall),
      spaceMedium: sp(base.spaceMedium),
      spaceLarge: sp(base.spaceLarge),
      bar: rad(base.bar),
      xs: rad(base.xs),
      sm: rad(base.sm),
      button: rad(base.button),
      input: rad(base.input),
      card: rad(base.card),
      window: rad(base.window),
      panel: rad(base.panel),
      bubble: rad(base.bubble),
      controlRadius: rad(base.controlRadius),
      surfaceRadius: rad(base.surfaceRadius),
      radiusSmall: rad(base.radiusSmall),
      fieldRadius: rad(base.fieldRadius),
      panelRadius: rad(base.panelRadius),
      modalRadius: rad(base.modalRadius),
      buttonHeight: sp(base.buttonHeight),
      buttonSmallHeight: sp(base.buttonSmallHeight),
      iconButtonSize: sp(base.iconButtonSize),
      inputHeight: sp(base.inputHeight),
      rowHeight: sp(base.rowHeight),
      fast: dur(base.fast),
      base: dur(base.base),
      slow: dur(base.slow),
      quick: dur(base.quick),
      standard: dur(base.standard),
    );
  }

  /// Builds the `mix` 2.5.0 [MixScope] token map synchronized with [toUiColors]
  /// and [toUiTokens].
  Map<MixToken, Object> toMixTokens() {
    final c = toUiColors();
    final t = toUiTokens();
    return <MixToken, Object>{
      UiMixTokens.accent: c.accent,
      UiMixTokens.accentHover: c.accentHover,
      UiMixTokens.accentTint: c.accentTint,
      UiMixTokens.accentCopy: c.accentCopy,
      UiMixTokens.onAccent: c.onAccent,
      UiMixTokens.focus: c.focus,
      UiMixTokens.page: c.page,
      UiMixTokens.elevated: c.elevated,
      UiMixTokens.surface: c.surface,
      UiMixTokens.recessed: c.recessed,
      UiMixTokens.strong: c.strong,
      UiMixTokens.body: c.body,
      UiMixTokens.hairline: c.hairline,
      UiMixTokens.done: c.done,
      UiMixTokens.working: c.working,
      UiMixTokens.error: c.error,
      UiMixTokens.spaceSmall: t.spaceSmall,
      UiMixTokens.spaceMedium: t.spaceMedium,
      UiMixTokens.spaceLarge: t.spaceLarge,
      UiMixTokens.controlRadius: Radius.circular(t.controlRadius),
      UiMixTokens.cardRadius: Radius.circular(t.card),
      UiMixTokens.surfaceRadius: Radius.circular(t.surfaceRadius),
      UiMixTokens.pillRadius: Radius.circular(t.pill),
      UiMixTokens.fastDuration: t.fast,
      UiMixTokens.baseDuration: t.base,
      UiMixTokens.slowDuration: t.slow,
    };
  }

  /// Builds a complete Material 3 [ThemeData] carrying both [UiColors] and
  /// [UiTokens] derived from these seeds.
  ThemeData toThemeData() =>
      UiTheme.fromColors(toUiColors(), tokens: toUiTokens());

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is UiThemeSeeds &&
          other.preset == preset &&
          other.accent == accent &&
          other.brightness == brightness &&
          other.density == density &&
          other.radiusScale == radiusScale &&
          other.motion == motion;

  @override
  int get hashCode =>
      Object.hash(preset, accent, brightness, density, radiusScale, motion);
}

/// Provides [Theme] (`UiColors` + `UiTokens`), `mix` [MixScope], and
/// [UiThemeScope] from a single [UiThemeSeeds] source of truth.
///
/// Set [wrapTheme] to `false` when mounted directly inside a `MaterialApp`
/// whose `theme` was already constructed via [UiThemeSeeds.toThemeData] so no
/// duplicate nested `Theme` scope is introduced.
final class UiSeedThemeScope extends StatelessWidget {
  const UiSeedThemeScope({
    super.key,
    required this.seeds,
    required this.child,
    this.wrapTheme = true,
  });

  final UiThemeSeeds seeds;
  final Widget child;
  final bool wrapTheme;

  @override
  Widget build(BuildContext context) {
    final scoped = MixScope(
      tokens: seeds.toMixTokens(),
      child: UiThemeScope(brightness: seeds.brightness, child: child),
    );
    if (!wrapTheme) return scoped;
    return Theme(data: seeds.toThemeData(), child: scoped);
  }
}
