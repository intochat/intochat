// ignore_for_file: prefer_initializing_formals

import 'dart:ui' show lerpDouble;

import 'package:flutter/material.dart';

import 'ui_tokens.dart';

/// Semantic colour tokens carried on [ThemeData.extensions].
///
/// Single authoritative palette registry for all 7 OS UI Identities
/// (`fluent-acrylic`, `spatial-glass`, `cyber-synaptic`, `warm-paper`,
/// `fluent-warm-mica`, `linear-carbon`, `nordic-tactical`) plus backward-compat
/// workspace palettes (`midnight`, `paper`, `lumen`).
final class UiColors extends ThemeExtension<UiColors> {
  const UiColors({
    required this.page,
    required this.elevated,
    required this.surface,
    required this.recessed,
    required this.ink,
    required this.strong,
    required this.body,
    required this.muted,
    required this.subtle,
    required this.onInk,
    required this.accent,
    required this.accentHover,
    required this.accentTint,
    required this.accentBorder,
    required this.onAccent,
    Color? accentCopy,
    Color? accentCopyHover,
    Color? focus,
    required this.done,
    required this.doneBg,
    required this.working,
    required this.workingBg,
    required this.error,
    required this.errorBg,
    required this.hairline,
    required this.borderStrong,
    required this.inputBorder,
    Color? userBubble,
    Color? userText,
    Color? assistant,
    Color? assistantText,
    Color? assistantBorder,
    Color? menuHover,
    this.preset = IcStylePreset.warmPaper,
    this.blurSigma = 0.0,
    this.windowOpacity = 1.0,
    Color? surfaceGlass,
    Color? surfaceGlassStrong,
    Color? specularBorder,
    Color? chrome,
    Color? chromeBorder,
    Color? titleBarFocused,
    Color? titleBarBlurred,
    Color? ambientGlow,
    Color? secondaryGlow,
    Color? chartContext,
    Color? idle,
    Color? needsYou,
    Color? needsYouBg,
    Color? hoverOverlay,
  }) : _accentCopy = accentCopy,
       _accentCopyHover = accentCopyHover,
       _focus = focus,
       _userBubble = userBubble,
       _userText = userText,
       _assistant = assistant,
       _assistantText = assistantText,
       _assistantBorder = assistantBorder,
       _menuHover = menuHover,
       _surfaceGlass = surfaceGlass,
       _surfaceGlassStrong = surfaceGlassStrong,
       _specularBorder = specularBorder,
       _chrome = chrome,
       _chromeBorder = chromeBorder,
       _titleBarFocused = titleBarFocused,
       _titleBarBlurred = titleBarBlurred,
       _ambientGlow = ambientGlow,
       _secondaryGlow = secondaryGlow,
       _chartContext = chartContext,
       _idle = idle,
       _needsYou = needsYou,
       _needsYouBg = needsYouBg,
       _hoverOverlay = hoverOverlay;

  final IcStylePreset preset;
  final Color page;
  final Color elevated;
  final Color surface;
  final Color recessed;
  final Color ink;

  final Color strong;
  final Color body;
  final Color muted;
  final Color subtle;
  final Color onInk;

  final Color accent;
  final Color accentHover;
  final Color accentTint;
  final Color accentBorder;
  final Color onAccent;
  final Color? _accentCopy;
  final Color? _accentCopyHover;
  final Color? _focus;

  /// Text-contrast accent (`--color-accent-text`). Defaults to [accent].
  Color get accentCopy => _accentCopy ?? accent;

  /// Hover partner for [accentCopy]. Defaults to [accentHover].
  Color get accentCopyHover => _accentCopyHover ?? accentHover;

  /// Translucent focus ring fill (`--color-accent-focus`).
  Color get focus => _focus ?? accent.withValues(alpha: 0.35);

  final Color done;
  final Color doneBg;
  final Color working;
  final Color workingBg;
  final Color error;
  final Color errorBg;

  final Color hairline;
  final Color borderStrong;
  final Color inputBorder;

  final Color? _userBubble;
  final Color? _userText;
  final Color? _assistant;
  final Color? _assistantText;
  final Color? _assistantBorder;
  final Color? _menuHover;

  Color get userBubble => _userBubble ?? strong;
  Color get userText => _userText ?? page;
  Color get assistant => _assistant ?? surface;
  Color get assistantText => _assistantText ?? body;
  Color get assistantBorder => _assistantBorder ?? hairline;
  Color get menuHover => _menuHover ?? recessed;

  final double blurSigma;
  final double windowOpacity;
  final Color? _surfaceGlass;
  final Color? _surfaceGlassStrong;
  final Color? _specularBorder;
  final Color? _chrome;
  final Color? _chromeBorder;
  final Color? _titleBarFocused;
  final Color? _titleBarBlurred;
  final Color? _ambientGlow;
  final Color? _secondaryGlow;
  final Color? _chartContext;
  final Color? _idle;
  final Color? _needsYou;
  final Color? _needsYouBg;
  final Color? _hoverOverlay;

  bool get isGlass => blurSigma > 0 && windowOpacity < 0.99;
  Color get surfaceGlass => _surfaceGlass ?? elevated;
  Color get surfaceGlassStrong => _surfaceGlassStrong ?? surface;
  Color get specularBorder => _specularBorder ?? hairline;
  Color get chrome => _chrome ?? _surfaceGlassStrong ?? elevated;
  Color get chromeBorder => _chromeBorder ?? hairline;
  Color get titleBarFocused => _titleBarFocused ?? elevated;
  Color get titleBarBlurred => _titleBarBlurred ?? recessed;
  Color get ambientGlow => _ambientGlow ?? accent;
  Color get secondaryGlow => _secondaryGlow ?? done;
  Color get chartContext => _chartContext ?? subtle;

  // --- Authoritative DESIGN.md §2 canonical token aliases & status/overlay pairs ---
  Color get surfacePage => page;
  Color get surfaceSunk => recessed;
  Color get surfaceCard => surface;
  Color get surfaceInk => ink;
  Color get surfaceElevated => elevated;

  Color get textStrong => strong;
  Color get textBody => body;
  Color get textMuted => muted;
  Color get textSubtle => subtle;

  Color get borderHairline => hairline;
  Color get borderInput => inputBorder;
  Color get ring => focus;

  Color get idle => _idle ?? subtle;
  Color get needsYou => _needsYou ?? accent;
  Color get needsYouBg => _needsYouBg ?? accentTint;
  Color get accentOnInk => const Color(0xFF8FB0FF);
  Color get bubbleMine => userBubble;
  Color get onBubbleMine => userText;
  Color get scrim => isDark ? const Color(0x80110F0B) : const Color(0x4717140F);

  /// Overlay hover & selection fills (`DESIGN.md` §2.8).
  Color get hoverOverlay =>
      _hoverOverlay ??
      (isDark ? const Color(0x14FBFAF7) : const Color(0x0A17140F));
  Color get hoverFill => hoverOverlay;
  Color get barHoverOverlay =>
      isDark ? const Color(0x1AFBFAF7) : const Color(0x0F17140F);
  Color get selectedOverlay =>
      isDark ? const Color(0x1FFBFAF7) : const Color(0x1217140F);

  /// Desktop background tones (`DESIGN.md` §2.7).
  Color get desktopCream =>
      isDark ? const Color(0xFF1D1914) : const Color(0xFFF2EFE9);
  Color get desktopSand =>
      isDark ? const Color(0xFF231E17) : const Color(0xFFE6DFD2);
  Color get desktopInk =>
      isDark ? const Color(0xFF0D0B08) : const Color(0xFF2A251E);

  Color desktopTone(DesktopTone tone) => switch (tone) {
    DesktopTone.cream => desktopCream,
    DesktopTone.sand => desktopSand,
    DesktopTone.ink => desktopInk,
  };

  /// Inferred brightness from [page] luminance.
  Brightness get brightness =>
      page.computeLuminance() < 0.5 ? Brightness.dark : Brightness.light;

  bool get isDark => brightness == Brightness.dark;

  static UiColors of(BuildContext context) {
    final theme = Theme.of(context);
    final ext = theme.extension<UiColors>();
    if (ext != null) return ext;
    return theme.brightness == Brightness.dark ? intochatDark : intochatLight;
  }

  /// Resolves the canonical palette for an [IcStylePreset] in light or dark mode.
  static UiColors forPreset(
    IcStylePreset preset, {
    bool dark = true,
  }) => switch (preset) {
    IcStylePreset.fluentAcrylic =>
      dark ? fluentAcrylicDark : fluentAcrylicLight,
    IcStylePreset.spatialGlass => dark ? spatialGlassDark : spatialGlassLight,
    IcStylePreset.cyberSynaptic =>
      dark ? cyberSynapticDark : cyberSynapticLight,
    IcStylePreset.warmPaper => dark ? warmPaperDark : warmPaperLight,
    IcStylePreset.fluentWarmMica =>
      dark ? fluentWarmMicaDark : fluentWarmMicaLight,
    IcStylePreset.linearCarbon => dark ? linearCarbonDark : linearCarbonLight,
    IcStylePreset.nordicTactical =>
      dark ? nordicTacticalDark : nordicTacticalLight,
  };

  /// Resolves a stored theme preference string (including legacy v1 names) into
  /// its canonical [UiColors] palette for [brightness].
  static UiColors forStoredTheme(
    String? stored, {
    required Brightness brightness,
  }) {
    final dark = brightness == Brightness.dark;
    return switch (stored) {
      'fluent-acrylic' => dark ? fluentAcrylicDark : fluentAcrylicLight,
      'spatial-glass' => dark ? spatialGlassDark : spatialGlassLight,
      'cyber-synaptic' => dark ? cyberSynapticDark : cyberSynapticLight,
      'warm-paper' || 'intochat' => dark ? warmPaperDark : warmPaperLight,
      'fluent-warm-mica' => dark ? fluentWarmMicaDark : fluentWarmMicaLight,
      'linear-carbon' => dark ? linearCarbonDark : linearCarbonLight,
      'nordic-tactical' => dark ? nordicTacticalDark : nordicTacticalLight,
      'lumen' => lumen,
      'dark' || 'midnight' => midnight,
      'light' || 'paper' => paper,
      _ => dark ? warmPaperDark : warmPaperLight,
    };
  }

  /// Warm Paper Light (Figma `:root`).
  static const intochatLight = UiColors(
    preset: IcStylePreset.warmPaper,
    page: Color(0xFFFBFAF7),
    elevated: Color(0xFFF5F3EE),
    surface: Color(0xFFFFFFFF),
    recessed: Color(0xFFF2EFE9),
    surfaceGlass: Color(0xE6FFFFFF),
    surfaceGlassStrong: Color(0xFFFFFFFF),
    ink: Color(0xFF17140F),
    strong: Color(0xFF17140F),
    body: Color(0xFF2A251E),
    muted: Color(0xFF7C7264),
    subtle: Color(0xFF9C9182),
    onInk: Color(0xFFFBFAF7),
    accent: Color(0xFF1F5EFF),
    accentHover: Color(0xFF1541C7),
    accentTint: Color(0xFFE7EEFF),
    accentBorder: Color(0x591F5EFF),
    onAccent: Color(0xFFFFFFFF),
    accentCopy: Color(0xFF1F5EFF),
    accentCopyHover: Color(0xFF1541C7),
    focus: Color(0x591F5EFF),
    done: Color(0xFF12A150),
    doneBg: Color(0xFFE1F6EA),
    working: Color(0xFFE8A317),
    workingBg: Color(0xFFFCF1DA),
    error: Color(0xFFE0402F),
    errorBg: Color(0xFFFCE7E3),
    hairline: Color(0xFFE4DFD5),
    borderStrong: Color(0xFF17140F),
    inputBorder: Color(0xFFC2B9AB),
    userBubble: Color(0xFF17140F),
    userText: Color(0xFFFBFAF7),
    assistant: Color(0xFFFFFFFF),
    assistantText: Color(0xFF2A251E),
    assistantBorder: Color(0xFFE4DFD5),
    menuHover: Color(0xFFF2EFE9),
    blurSigma: 0.0,
    windowOpacity: 1.0,
    specularBorder: Color(0xFFE4DFD5),
    chrome: Color(0xE6FFFFFF),
    chromeBorder: Color(0xFFE4DFD5),
    titleBarFocused: Color(0xFFF2EFE9),
    titleBarBlurred: Color(0xFFFBFAF7),
    ambientGlow: Color(0xFF1F5EFF),
    secondaryGlow: Color(0xFF12A150),
    chartContext: Color(0xFF9C9182),
  );

  /// Warm Paper Dark (`[data-theme="dark"]`).
  static const intochatDark = UiColors(
    preset: IcStylePreset.warmPaper,
    page: Color(0xFF17140F),
    elevated: Color(0xFF1F1B15),
    surface: Color(0xFF2A251E),
    recessed: Color(0xFF110F0B),
    surfaceGlass: Color(0xE62A251E),
    surfaceGlassStrong: Color(0xFF2A251E),
    ink: Color(0xFF110F0B),
    strong: Color(0xFFFBFAF7),
    body: Color(0xFFF0ECE4),
    muted: Color(0xFF9C9182),
    subtle: Color(0xFF7C7264),
    onInk: Color(0xFFFBFAF7),
    accent: Color(0xFF4B7DFF),
    accentHover: Color(0xFF1F5EFF),
    accentTint: Color(0x294B7DFF),
    accentBorder: Color(0x734B7DFF),
    onAccent: Color(0xFFFFFFFF),
    accentCopy: Color(0xFFFBFAF7),
    accentCopyHover: Color(0xFFF0ECE4),
    focus: Color(0x734B7DFF),
    done: Color(0xFF8FE0AF),
    doneBg: Color(0x2912A150),
    working: Color(0xFFF6D28A),
    workingBg: Color(0x29E8A317),
    error: Color(0xFFF3A99F),
    errorBg: Color(0x29E0402F),
    hairline: Color(0xFF423B31),
    borderStrong: Color(0xFFF0ECE4),
    inputBorder: Color(0xFF5E554A),
    userBubble: Color(0xFFFBFAF7),
    userText: Color(0xFF17140F),
    assistant: Color(0xFF2A251E),
    assistantText: Color(0xFFF0ECE4),
    assistantBorder: Color(0xFF423B31),
    menuHover: Color(0x99423B31),
    blurSigma: 0.0,
    windowOpacity: 1.0,
    specularBorder: Color(0xFF423B31),
    chrome: Color(0xE62A251E),
    chromeBorder: Color(0xFF423B31),
    titleBarFocused: Color(0xFF332D25),
    titleBarBlurred: Color(0xFF2A251E),
    ambientGlow: Color(0xFF4B7DFF),
    secondaryGlow: Color(0xFF8FE0AF),
    chartContext: Color(0xFF9C9182),
  );

  static const warmPaperLight = intochatLight;
  static const warmPaperDark = intochatDark;

  static const fluentAcrylicDark = UiColors(
    preset: IcStylePreset.fluentAcrylic,
    page: Color(0xFF11131A),
    elevated: Color(0xFF171B26),
    surface: Color(0xD91B1F2B),
    recessed: Color(0xCC0B0D13),
    surfaceGlass: Color(0xBF171B26),
    surfaceGlassStrong: Color(0xD91B1F2B),
    ink: Color(0xFF090B10),
    strong: Color(0xFFF8FAFC),
    body: Color(0xFFE2E8F0),
    muted: Color(0xFF94A3B8),
    subtle: Color(0xFF64748B),
    onInk: Color(0xFFF8FAFC),
    accent: Color(0xFF3B82F6),
    accentHover: Color(0xFF2563EB),
    accentTint: Color(0x333B82F6),
    accentBorder: Color(0x733B82F6),
    onAccent: Color(0xFFFFFFFF),
    focus: Color(0x733B82F6),
    done: Color(0xFF34D399),
    doneBg: Color(0x2910B981),
    working: Color(0xFFFBBF24),
    workingBg: Color(0x29F59E0B),
    error: Color(0xFFF87171),
    errorBg: Color(0x29EF4444),
    hairline: Color(0xFF2A3040),
    borderStrong: Color(0xFFF1F5F9),
    inputBorder: Color(0xFF3E475E),
    specularBorder: Color(0x2EFFFFFF),
    blurSigma: 26.0,
    windowOpacity: 0.82,
    chrome: Color(0xBF171B26),
    chromeBorder: Color(0x38FFFFFF),
    titleBarFocused: Color(0xD9222736),
    titleBarBlurred: Color(0xBF181C26),
    ambientGlow: Color(0xFF3B82F6),
    secondaryGlow: Color(0xFF06B6D4),
    chartContext: Color(0xFF94A3B8),
  );

  static const fluentAcrylicLight = UiColors(
    preset: IcStylePreset.fluentAcrylic,
    page: Color(0xFFF7F6F2),
    elevated: Color(0xCCFFFFFF),
    surface: Color(0xE8FFFFFF),
    recessed: Color(0xDAEDEAE2),
    surfaceGlass: Color(0xCCFFFFFF),
    surfaceGlassStrong: Color(0xE8FFFFFF),
    ink: Color(0xFF14161D),
    strong: Color(0xFF12141A),
    body: Color(0xFF252830),
    muted: Color(0xFF686D7A),
    subtle: Color(0xFF8E94A2),
    onInk: Color(0xFFFFFFFF),
    accent: Color(0xFF2563EB),
    accentHover: Color(0xFF1D4ED8),
    accentTint: Color(0xFFE0EAFF),
    accentBorder: Color(0x592563EB),
    onAccent: Color(0xFFFFFFFF),
    focus: Color(0x592563EB),
    done: Color(0xFF10B981),
    doneBg: Color(0xFFDFF9EE),
    working: Color(0xFFF59E0B),
    workingBg: Color(0xFFFEF3C7),
    error: Color(0xFFEF4444),
    errorBg: Color(0xFFFEE2E2),
    hairline: Color(0xFFDDE0E7),
    borderStrong: Color(0xFF12141A),
    inputBorder: Color(0xFFB8BECB),
    specularBorder: Color(0x99FFFFFF),
    blurSigma: 24.0,
    windowOpacity: 0.86,
    chrome: Color(0xCCFFFFFF),
    chromeBorder: Color(0xB3FFFFFF),
    titleBarFocused: Color(0xD9F1EFE9),
    titleBarBlurred: Color(0xCCF8F7F4),
    ambientGlow: Color(0xFF2563EB),
    secondaryGlow: Color(0xFF0EA5E9),
    chartContext: Color(0xFF8E94A2),
  );

  static const spatialGlassDark = UiColors(
    preset: IcStylePreset.spatialGlass,
    page: Color(0xB80B101B),
    elevated: Color(0xA6131C2E),
    surface: Color(0xC7161F30),
    recessed: Color(0xB3070A12),
    surfaceGlass: Color(0xA6131C2E),
    surfaceGlassStrong: Color(0xC7161F30),
    ink: Color(0xFF060911),
    strong: Color(0xFFF8FAFC),
    body: Color(0xFFE2E8F0),
    muted: Color(0xFF94A3B8),
    subtle: Color(0xFF64748B),
    onInk: Color(0xFFF8FAFC),
    accent: Color(0xFF38BDF8),
    accentHover: Color(0xFF0284C7),
    accentTint: Color(0x3338BDF8),
    accentBorder: Color(0x7338BDF8),
    onAccent: Color(0xFF082F49),
    focus: Color(0x7338BDF8),
    done: Color(0xFF34D399),
    doneBg: Color(0x2910B981),
    working: Color(0xFFFBBF24),
    workingBg: Color(0x29F59E0B),
    error: Color(0xFFFB7185),
    errorBg: Color(0x29E11D48),
    hairline: Color(0x4094A3B8),
    borderStrong: Color(0xFFF8FAFC),
    inputBorder: Color(0x6694A3B8),
    specularBorder: Color(0x45FFFFFF),
    blurSigma: 36.0,
    windowOpacity: 0.72,
    chrome: Color(0xA6131C2E),
    chromeBorder: Color(0x4DFFFFFF),
    titleBarFocused: Color(0xCC1E293B),
    titleBarBlurred: Color(0xA6131C2E),
    ambientGlow: Color(0xFF38BDF8),
    secondaryGlow: Color(0xFF818CF8),
    chartContext: Color(0xFF94A3B8),
  );

  static const spatialGlassLight = UiColors(
    preset: IcStylePreset.spatialGlass,
    page: Color(0xCCF1F5F9),
    elevated: Color(0xB8FFFFFF),
    surface: Color(0xD9FFFFFF),
    recessed: Color(0xB3E2E8F0),
    surfaceGlass: Color(0xB8FFFFFF),
    surfaceGlassStrong: Color(0xD9FFFFFF),
    ink: Color(0xFF0F172A),
    strong: Color(0xFF0F172A),
    body: Color(0xFF1E293B),
    muted: Color(0xFF64748B),
    subtle: Color(0xFF94A3B8),
    onInk: Color(0xFFFFFFFF),
    accent: Color(0xFF0284C7),
    accentHover: Color(0xFF0369A1),
    accentTint: Color(0xFFE0F2FE),
    accentBorder: Color(0x590284C7),
    onAccent: Color(0xFFFFFFFF),
    focus: Color(0x590284C7),
    done: Color(0xFF059669),
    doneBg: Color(0xFFD1FAE5),
    working: Color(0xFFD97706),
    workingBg: Color(0xFFFEF3C7),
    error: Color(0xFFE11D48),
    errorBg: Color(0xFFFFE4E6),
    hairline: Color(0x99CBD5E1),
    borderStrong: Color(0xFF0F172A),
    inputBorder: Color(0xFF94A3B8),
    specularBorder: Color(0xCCFFFFFF),
    blurSigma: 34.0,
    windowOpacity: 0.74,
    chrome: Color(0xB8FFFFFF),
    chromeBorder: Color(0xCCFFFFFF),
    titleBarFocused: Color(0xCCE2E8F0),
    titleBarBlurred: Color(0xB3F1F5F9),
    ambientGlow: Color(0xFF0284C7),
    secondaryGlow: Color(0xFF6366F1),
    chartContext: Color(0xFF64748B),
  );

  static const cyberSynapticDark = UiColors(
    preset: IcStylePreset.cyberSynaptic,
    page: Color(0xFF060B0E),
    elevated: Color(0xBF091217),
    surface: Color(0xD90C151B),
    recessed: Color(0xD9040709),
    surfaceGlass: Color(0xBF091217),
    surfaceGlassStrong: Color(0xD90C151B),
    ink: Color(0xFF030608),
    strong: Color(0xFFECFDF5),
    body: Color(0xFFD1FAE5),
    muted: Color(0xFF6EE7B7),
    subtle: Color(0xFF34D399),
    onInk: Color(0xFFECFDF5),
    accent: Color(0xFF10B981),
    accentHover: Color(0xFF059669),
    accentTint: Color(0x2910B981),
    accentBorder: Color(0x7310B981),
    onAccent: Color(0xFF022C22),
    focus: Color(0x7310B981),
    done: Color(0xFF34D399),
    doneBg: Color(0x2910B981),
    working: Color(0xFFFBBF24),
    workingBg: Color(0x29F59E0B),
    error: Color(0xFFF87171),
    errorBg: Color(0x29EF4444),
    hairline: Color(0xFF132A2E),
    borderStrong: Color(0xFF34D399),
    inputBorder: Color(0xFF1E464B),
    specularBorder: Color(0x4010B981),
    blurSigma: 28.0,
    windowOpacity: 0.80,
    chrome: Color(0xBF091217),
    chromeBorder: Color(0x4D10B981),
    titleBarFocused: Color(0xD90F1E26),
    titleBarBlurred: Color(0xBF091217),
    ambientGlow: Color(0xFF10B981),
    secondaryGlow: Color(0xFF06B6D4),
    chartContext: Color(0xFF06B6D4),
  );

  static const cyberSynapticLight = fluentAcrylicLight;

  static const fluentWarmMicaDark = UiColors(
    preset: IcStylePreset.fluentWarmMica,
    page: Color(0xFF141210),
    elevated: Color(0xFF1E1B18),
    surface: Color(0xFF25211D),
    recessed: Color(0xFF181512),
    surfaceGlass: Color(0xCC1E1B18),
    surfaceGlassStrong: Color(0xE625211D),
    ink: Color(0xFFFAF7F2),
    strong: Color(0xFFFAF7F2),
    body: Color(0xFFE6E0D6),
    muted: Color(0xFFA69D90),
    subtle: Color(0xFF787065),
    onInk: Color(0xFF141210),
    accent: Color(0xFFF59E0B),
    accentHover: Color(0xFFFBBF24),
    accentTint: Color(0x26F59E0B),
    accentBorder: Color(0x66F59E0B),
    onAccent: Color(0xFF141210),
    focus: Color(0x66F59E0B),
    done: Color(0xFF34D399),
    doneBg: Color(0x2234D399),
    working: Color(0xFFFBBF24),
    workingBg: Color(0x24FBBF24),
    error: Color(0xFFF87171),
    errorBg: Color(0x24F87171),
    hairline: Color(0xFF2E2924),
    borderStrong: Color(0xFF423B34),
    inputBorder: Color(0xFF38322C),
    specularBorder: Color(0x22FFFFFF),
    blurSigma: 26.0,
    windowOpacity: 0.86,
    ambientGlow: Color(0xFFF59E0B),
  );

  static const fluentWarmMicaLight = UiColors(
    preset: IcStylePreset.fluentWarmMica,
    page: Color(0xFFF7F4EF),
    elevated: Color(0xFFFFFFFF),
    surface: Color(0xFFFFFFFF),
    recessed: Color(0xFFEDE8DF),
    surfaceGlass: Color(0xD9FFFFFF),
    surfaceGlassStrong: Color(0xF0FFFFFF),
    ink: Color(0xFF1C1917),
    strong: Color(0xFF1C1917),
    body: Color(0xFF292524),
    muted: Color(0xFF57534E),
    subtle: Color(0xFF78716C),
    onInk: Color(0xFFFFFFFF),
    accent: Color(0xFFD97706),
    accentHover: Color(0xFFB45309),
    accentTint: Color(0x1FD97706),
    accentBorder: Color(0x59D97706),
    onAccent: Color(0xFFFFFFFF),
    focus: Color(0x59D97706),
    done: Color(0xFF059669),
    doneBg: Color(0x18059669),
    working: Color(0xFFD97706),
    workingBg: Color(0x18D97706),
    error: Color(0xFFDC2626),
    errorBg: Color(0x18DC2626),
    hairline: Color(0xFFE5DFD5),
    borderStrong: Color(0xFFD6CEC2),
    inputBorder: Color(0xFFDDD6CA),
    specularBorder: Color(0xB3FFFFFF),
    blurSigma: 22.0,
    windowOpacity: 0.88,
    ambientGlow: Color(0xFFD97706),
  );

  static const linearCarbonDark = UiColors(
    preset: IcStylePreset.linearCarbon,
    page: Color(0xFF0D0E12),
    elevated: Color(0xFF111317),
    surface: Color(0xFF15171E),
    recessed: Color(0xFF090A0D),
    surfaceGlass: Color(0xCC111317),
    surfaceGlassStrong: Color(0xE615171E),
    ink: Color(0xFF07080A),
    strong: Color(0xFFF4F5F8),
    body: Color(0xFFD8DAE0),
    muted: Color(0xFF8A8F9E),
    subtle: Color(0xFF5C606E),
    onInk: Color(0xFFF4F5F8),
    accent: Color(0xFF5E6AD2),
    accentHover: Color(0xFF4E5AC0),
    accentTint: Color(0x2E5E6AD2),
    accentBorder: Color(0x735E6AD2),
    onAccent: Color(0xFFFFFFFF),
    focus: Color(0x735E6AD2),
    done: Color(0xFF4CB782),
    doneBg: Color(0x294CB782),
    working: Color(0xFFF2C94C),
    workingBg: Color(0x29F2C94C),
    error: Color(0xFFEB5757),
    errorBg: Color(0x29EB5757),
    hairline: Color(0xFF232630),
    borderStrong: Color(0xFFF4F5F8),
    inputBorder: Color(0xFF343846),
    specularBorder: Color(0x24FFFFFF),
    blurSigma: 20.0,
    windowOpacity: 0.92,
    chrome: Color(0xD914161D),
    chromeBorder: Color(0xFF262934),
    titleBarFocused: Color(0xFF1B1E27),
    titleBarBlurred: Color(0xFF13151B),
    ambientGlow: Color(0xFF5E6AD2),
    secondaryGlow: Color(0xFF818CF8),
    chartContext: Color(0xFF8A8F9E),
  );

  static const linearCarbonLight = warmPaperLight;

  static const nordicTacticalDark = UiColors(
    preset: IcStylePreset.nordicTactical,
    page: Color(0xFF0F1419),
    elevated: Color(0xFF151C24),
    surface: Color(0xFF1B242E),
    recessed: Color(0xFF0B0F14),
    surfaceGlass: Color(0xCC151C24),
    surfaceGlassStrong: Color(0xE61B242E),
    ink: Color(0xFFE6EDF3),
    strong: Color(0xFFE6EDF3),
    body: Color(0xFFC9D1D9),
    muted: Color(0xFF8B949E),
    subtle: Color(0xFF6E7681),
    onInk: Color(0xFF0F1419),
    accent: Color(0xFF38BDF8),
    accentHover: Color(0xFF7DD3FC),
    accentTint: Color(0x2438BDF8),
    accentBorder: Color(0x6638BDF8),
    onAccent: Color(0xFF0F1419),
    focus: Color(0x6638BDF8),
    done: Color(0xFF3FB950),
    doneBg: Color(0x223FB950),
    working: Color(0xFFD29922),
    workingBg: Color(0x24D29922),
    error: Color(0xFFF85149),
    errorBg: Color(0x24F85149),
    hairline: Color(0xFF263240),
    borderStrong: Color(0xFF38485C),
    inputBorder: Color(0xFF303E50),
    specularBorder: Color(0x2438BDF8),
    blurSigma: 18.0,
    windowOpacity: 0.88,
    ambientGlow: Color(0xFF38BDF8),
  );

  static const nordicTacticalLight = UiColors(
    preset: IcStylePreset.nordicTactical,
    page: Color(0xFFF0F4F8),
    elevated: Color(0xFFFFFFFF),
    surface: Color(0xFFFFFFFF),
    recessed: Color(0xFFE2E8F0),
    surfaceGlass: Color(0xD9FFFFFF),
    surfaceGlassStrong: Color(0xF0FFFFFF),
    ink: Color(0xFF0F172A),
    strong: Color(0xFF0F172A),
    body: Color(0xFF1E293B),
    muted: Color(0xFF475569),
    subtle: Color(0xFF64748B),
    onInk: Color(0xFFFFFFFF),
    accent: Color(0xFF0284C7),
    accentHover: Color(0xFF0369A1),
    accentTint: Color(0x1F0284C7),
    accentBorder: Color(0x590284C7),
    onAccent: Color(0xFFFFFFFF),
    focus: Color(0x590284C7),
    done: Color(0xFF16A34A),
    doneBg: Color(0x1816A34A),
    working: Color(0xFFD97706),
    workingBg: Color(0x18D97706),
    error: Color(0xFFDC2626),
    errorBg: Color(0x18DC2626),
    hairline: Color(0xFFCBD5E1),
    borderStrong: Color(0xFF94A3B8),
    inputBorder: Color(0xFFCBD5E1),
    specularBorder: Color(0xB3FFFFFF),
    blurSigma: 16.0,
    windowOpacity: 0.90,
    ambientGlow: Color(0xFF0284C7),
  );

  // --- Legacy v1 workspace palettes kept for backward-compat preferences ---
  static const midnight = UiColors(
    page: Color(0xFF141720),
    elevated: Color(0xE61B1F2A),
    surface: Color(0xFF1B1F2A),
    recessed: Color(0xFF11141B),
    ink: Color(0xFF11141B),
    strong: Color(0xFFE9EBF2),
    body: Color(0xFFE9EBF2),
    muted: Color(0xFF969CAF),
    subtle: Color(0xFF5E6474),
    onInk: Color(0xFF141720),
    accent: Color(0xFF7B9BE3),
    accentHover: Color(0xFF7B9BE3),
    accentTint: Color(0x297B9BE3),
    accentBorder: Color(0x597B9BE3),
    onAccent: Color(0xFF141720),
    accentCopy: Color(0xFF7B9BE3),
    accentCopyHover: Color(0xFF7B9BE3),
    focus: Color(0x597B9BE3),
    done: Color(0xFF65C5A0),
    doneBg: Color(0x2965C5A0),
    working: Color(0xFFE09261),
    workingBg: Color(0x29E09261),
    error: Color(0xFFFFB4AB),
    errorBg: Color(0x29FFB4AB),
    hairline: Color(0xFF292E3B),
    borderStrong: Color(0xFFE9EBF2),
    inputBorder: Color(0xFF363C4C),
    userBubble: Color(0xFFE9EBF2),
    userText: Color(0xFF141720),
    assistant: Color(0xFF1B1F2A),
    assistantText: Color(0xFFE9EBF2),
    assistantBorder: Color(0xFF292E3B),
    menuHover: Color(0xFF11141B),
    blurSigma: 18.0,
    windowOpacity: 0.94,
    specularBorder: Color(0xFF292E3B),
    chrome: Color(0xE61B1F2A),
    chromeBorder: Color(0xFF292E3B),
    titleBarFocused: Color(0xFF222736),
    titleBarBlurred: Color(0xFF1B1F2A),
    ambientGlow: Color(0xFF7B9BE3),
    secondaryGlow: Color(0xFF65C5A0),
    chartContext: Color(0xFF969CAF),
  );

  static const paper = UiColors(
    page: Color(0xFFF7F7F2),
    elevated: Color(0xE6FFFCF7),
    surface: Color(0xFFFFFFFF),
    recessed: Color(0xFFF0F3EC),
    ink: Color(0xFF263934),
    strong: Color(0xFF263934),
    body: Color(0xFF263934),
    muted: Color(0xFF687970),
    subtle: Color(0xFF88958D),
    onInk: Color(0xFFFFFFFF),
    accent: Color(0xFF397B63),
    accentHover: Color(0xFF397B63),
    accentTint: Color(0xFFE4EEE5),
    accentBorder: Color(0xFFB9CBC0),
    onAccent: Color(0xFFFFFFFF),
    accentCopy: Color(0xFF397B63),
    accentCopyHover: Color(0xFF397B63),
    focus: Color(0x59397B63),
    done: Color(0xFF397B63),
    doneBg: Color(0xFFE4EEE5),
    working: Color(0xFF986A37),
    workingBg: Color(0x29986A37),
    error: Color(0xFFAD4F3F),
    errorBg: Color(0x29AD4F3F),
    hairline: Color(0xFFDBE2DA),
    borderStrong: Color(0xFF263934),
    inputBorder: Color(0xFFB9CBC0),
    userBubble: Color(0xFF263934),
    userText: Color(0xFFF7F7F2),
    assistant: Color(0xFFFFFFFF),
    assistantText: Color(0xFF263934),
    assistantBorder: Color(0xFFDBE2DA),
    menuHover: Color(0xFFF0F3EC),
    blurSigma: 0.0,
    windowOpacity: 1.0,
    specularBorder: Color(0xFFDBE2DA),
    chrome: Color(0xE6FFFCF7),
    chromeBorder: Color(0xFFDBE2DA),
    titleBarFocused: Color(0xFFF0F3EC),
    titleBarBlurred: Color(0xFFF7F7F2),
    ambientGlow: Color(0xFF397B63),
    secondaryGlow: Color(0xFF12A150),
    chartContext: Color(0xFF687970),
  );

  static const lumen = UiColors(
    page: Color(0xFF060A08),
    elevated: Color(0xE60B120F),
    surface: Color(0xFF0B120F),
    recessed: Color(0xFF0F1813),
    ink: Color(0xFF0F1813),
    strong: Color(0xFFE3EFE8),
    body: Color(0xFFE3EFE8),
    muted: Color(0xFF8CA397),
    subtle: Color(0xFF5F776A),
    onInk: Color(0xFF060A08),
    accent: Color(0xFF7CE0A8),
    accentHover: Color(0xFFA5F0C4),
    accentTint: Color(0xFF12301F),
    accentBorder: Color(0x597CE0A8),
    onAccent: Color(0xFF04170D),
    accentCopy: Color(0xFF7CE0A8),
    accentCopyHover: Color(0xFF7CE0A8),
    focus: Color(0x597CE0A8),
    done: Color(0xFF7CE0A8),
    doneBg: Color(0x297CE0A8),
    working: Color(0xFFD9A867),
    workingBg: Color(0x29D9A867),
    error: Color(0xFFFFB4AB),
    errorBg: Color(0x29FFB4AB),
    hairline: Color(0xFF1D2D24),
    borderStrong: Color(0xFFE3EFE8),
    inputBorder: Color(0xFF3F5A4A),
    userBubble: Color(0xFFE3EFE8),
    userText: Color(0xFF060A08),
    assistant: Color(0xFF0B120F),
    assistantText: Color(0xFFE3EFE8),
    assistantBorder: Color(0xFF1D2D24),
    menuHover: Color(0xFF0F1813),
    blurSigma: 16.0,
    windowOpacity: 0.94,
    specularBorder: Color(0xFF1D2D24),
    chrome: Color(0xE60B120F),
    chromeBorder: Color(0xFF1D2D24),
    titleBarFocused: Color(0xFF131E18),
    titleBarBlurred: Color(0xFF0B120F),
    ambientGlow: Color(0xFF7CE0A8),
    secondaryGlow: Color(0xFF34D399),
    chartContext: Color(0xFF8CA397),
  );

  @override
  UiColors copyWith({
    IcStylePreset? preset,
    Color? page,
    Color? elevated,
    Color? surface,
    Color? recessed,
    Color? ink,
    Color? strong,
    Color? body,
    Color? muted,
    Color? subtle,
    Color? onInk,
    Color? accent,
    Color? accentHover,
    Color? accentTint,
    Color? accentBorder,
    Color? onAccent,
    Color? accentCopy,
    Color? accentCopyHover,
    Color? focus,
    Color? done,
    Color? doneBg,
    Color? working,
    Color? workingBg,
    Color? error,
    Color? errorBg,
    Color? hairline,
    Color? borderStrong,
    Color? inputBorder,
    Color? userBubble,
    Color? userText,
    Color? assistant,
    Color? assistantText,
    Color? assistantBorder,
    Color? menuHover,
    double? blurSigma,
    double? windowOpacity,
    Color? surfaceGlass,
    Color? surfaceGlassStrong,
    Color? specularBorder,
    Color? chrome,
    Color? chromeBorder,
    Color? titleBarFocused,
    Color? titleBarBlurred,
    Color? ambientGlow,
    Color? secondaryGlow,
    Color? chartContext,
    Color? idle,
    Color? needsYou,
    Color? needsYouBg,
    Color? hoverOverlay,
  }) => UiColors(
    preset: preset ?? this.preset,
    page: page ?? this.page,
    elevated: elevated ?? this.elevated,
    surface: surface ?? this.surface,
    recessed: recessed ?? this.recessed,
    ink: ink ?? this.ink,
    strong: strong ?? this.strong,
    body: body ?? this.body,
    muted: muted ?? this.muted,
    subtle: subtle ?? this.subtle,
    onInk: onInk ?? this.onInk,
    accent: accent ?? this.accent,
    accentHover: accentHover ?? this.accentHover,
    accentTint: accentTint ?? this.accentTint,
    accentBorder: accentBorder ?? this.accentBorder,
    onAccent: onAccent ?? this.onAccent,
    accentCopy: accentCopy ?? _accentCopy,
    accentCopyHover: accentCopyHover ?? _accentCopyHover,
    focus: focus ?? _focus,
    done: done ?? this.done,
    doneBg: doneBg ?? this.doneBg,
    working: working ?? this.working,
    workingBg: workingBg ?? this.workingBg,
    error: error ?? this.error,
    errorBg: errorBg ?? this.errorBg,
    hairline: hairline ?? this.hairline,
    borderStrong: borderStrong ?? this.borderStrong,
    inputBorder: inputBorder ?? this.inputBorder,
    userBubble: userBubble ?? _userBubble,
    userText: userText ?? _userText,
    assistant: assistant ?? _assistant,
    assistantText: assistantText ?? _assistantText,
    assistantBorder: assistantBorder ?? _assistantBorder,
    menuHover: menuHover ?? _menuHover,
    blurSigma: blurSigma ?? this.blurSigma,
    windowOpacity: windowOpacity ?? this.windowOpacity,
    surfaceGlass: surfaceGlass ?? _surfaceGlass,
    surfaceGlassStrong: surfaceGlassStrong ?? _surfaceGlassStrong,
    specularBorder: specularBorder ?? _specularBorder,
    chrome: chrome ?? _chrome,
    chromeBorder: chromeBorder ?? _chromeBorder,
    titleBarFocused: titleBarFocused ?? _titleBarFocused,
    titleBarBlurred: titleBarBlurred ?? _titleBarBlurred,
    ambientGlow: ambientGlow ?? _ambientGlow,
    secondaryGlow: secondaryGlow ?? _secondaryGlow,
    chartContext: chartContext ?? _chartContext,
    idle: idle ?? _idle,
    needsYou: needsYou ?? _needsYou,
    needsYouBg: needsYouBg ?? _needsYouBg,
    hoverOverlay: hoverOverlay ?? _hoverOverlay,
  );

  @override
  UiColors lerp(ThemeExtension<UiColors>? other, double t) {
    if (other is! UiColors) return this;
    Color c(Color a, Color b) => Color.lerp(a, b, t)!;
    return UiColors(
      preset: t < 0.5 ? preset : other.preset,
      page: c(page, other.page),
      elevated: c(elevated, other.elevated),
      surface: c(surface, other.surface),
      recessed: c(recessed, other.recessed),
      ink: c(ink, other.ink),
      strong: c(strong, other.strong),
      body: c(body, other.body),
      muted: c(muted, other.muted),
      subtle: c(subtle, other.subtle),
      onInk: c(onInk, other.onInk),
      accent: c(accent, other.accent),
      accentHover: c(accentHover, other.accentHover),
      accentTint: c(accentTint, other.accentTint),
      accentBorder: c(accentBorder, other.accentBorder),
      onAccent: c(onAccent, other.onAccent),
      accentCopy: c(accentCopy, other.accentCopy),
      accentCopyHover: c(accentCopyHover, other.accentCopyHover),
      focus: c(focus, other.focus),
      done: c(done, other.done),
      doneBg: c(doneBg, other.doneBg),
      working: c(working, other.working),
      workingBg: c(workingBg, other.workingBg),
      error: c(error, other.error),
      errorBg: c(errorBg, other.errorBg),
      hairline: c(hairline, other.hairline),
      borderStrong: c(borderStrong, other.borderStrong),
      inputBorder: c(inputBorder, other.inputBorder),
      userBubble: c(userBubble, other.userBubble),
      userText: c(userText, other.userText),
      assistant: c(assistant, other.assistant),
      assistantText: c(assistantText, other.assistantText),
      assistantBorder: c(assistantBorder, other.assistantBorder),
      menuHover: c(menuHover, other.menuHover),
      blurSigma: lerpDouble(blurSigma, other.blurSigma, t)!,
      windowOpacity: lerpDouble(windowOpacity, other.windowOpacity, t)!,
      surfaceGlass: c(surfaceGlass, other.surfaceGlass),
      surfaceGlassStrong: c(surfaceGlassStrong, other.surfaceGlassStrong),
      specularBorder: c(specularBorder, other.specularBorder),
      chrome: c(chrome, other.chrome),
      chromeBorder: c(chromeBorder, other.chromeBorder),
      titleBarFocused: c(titleBarFocused, other.titleBarFocused),
      titleBarBlurred: c(titleBarBlurred, other.titleBarBlurred),
      ambientGlow: c(ambientGlow, other.ambientGlow),
      secondaryGlow: c(secondaryGlow, other.secondaryGlow),
      chartContext: c(chartContext, other.chartContext),
      idle: c(idle, other.idle),
      needsYou: c(needsYou, other.needsYou),
      needsYouBg: c(needsYouBg, other.needsYouBg),
      hoverOverlay: c(hoverOverlay, other.hoverOverlay),
    );
  }

  @override
  bool operator ==(Object other) =>
      other is UiColors &&
      other.preset == preset &&
      other.page == page &&
      other.elevated == elevated &&
      other.surface == surface &&
      other.recessed == recessed &&
      other.ink == ink &&
      other.strong == strong &&
      other.body == body &&
      other.muted == muted &&
      other.subtle == subtle &&
      other.onInk == onInk &&
      other.accent == accent &&
      other.accentHover == accentHover &&
      other.accentTint == accentTint &&
      other.accentBorder == accentBorder &&
      other.onAccent == onAccent &&
      other.accentCopy == accentCopy &&
      other.accentCopyHover == accentCopyHover &&
      other.focus == focus &&
      other.done == done &&
      other.doneBg == doneBg &&
      other.working == working &&
      other.workingBg == workingBg &&
      other.error == error &&
      other.errorBg == errorBg &&
      other.hairline == hairline &&
      other.borderStrong == borderStrong &&
      other.inputBorder == inputBorder &&
      other.userBubble == userBubble &&
      other.userText == userText &&
      other.assistant == assistant &&
      other.assistantText == assistantText &&
      other.assistantBorder == assistantBorder &&
      other.menuHover == menuHover &&
      other.blurSigma == blurSigma &&
      other.windowOpacity == windowOpacity &&
      other.surfaceGlass == surfaceGlass &&
      other.surfaceGlassStrong == surfaceGlassStrong &&
      other.specularBorder == specularBorder &&
      other.chrome == chrome &&
      other.chromeBorder == chromeBorder &&
      other.titleBarFocused == titleBarFocused &&
      other.titleBarBlurred == titleBarBlurred &&
      other.ambientGlow == ambientGlow &&
      other.secondaryGlow == secondaryGlow &&
      other.chartContext == chartContext;

  @override
  int get hashCode => Object.hashAll([
    preset,
    page,
    elevated,
    surface,
    recessed,
    ink,
    strong,
    body,
    muted,
    subtle,
    onInk,
    accent,
    accentHover,
    accentTint,
    accentBorder,
    onAccent,
    accentCopy,
    accentCopyHover,
    focus,
    done,
    doneBg,
    working,
    workingBg,
    error,
    errorBg,
    hairline,
    borderStrong,
    inputBorder,
    userBubble,
    userText,
    assistant,
    assistantText,
    assistantBorder,
    menuHover,
    blurSigma,
    windowOpacity,
    surfaceGlass,
    surfaceGlassStrong,
    specularBorder,
    chrome,
    chromeBorder,
    titleBarFocused,
    titleBarBlurred,
    ambientGlow,
    secondaryGlow,
    chartContext,
  ]);
}
