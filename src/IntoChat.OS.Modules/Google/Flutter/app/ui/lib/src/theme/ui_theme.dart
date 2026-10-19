import 'package:flutter/material.dart';
import 'package:flutter_chat_core/flutter_chat_core.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;

import '../lumen/lumen_palette.dart';
import 'ui_colors.dart';
import 'ui_tokens.dart';

/// Product color tokens. Shell and ui both consume these — do not re-declare
/// palettes in the shell package.
abstract final class UiPalette {
  static const navigation = Color(0xFF101219);
  static const surface = Color(0xFF141720);
  static const surfaceRaised = Color(0xFF1B1F2A);
  static const surfaceSunken = Color(0xFF11141B);
  static const line = Color(0xFF292E3B);
  static const lineStrong = Color(0xFF363C4C);
  static const textPrimary = Color(0xFFE9EBF2);
  static const textMuted = Color(0xFF969CAF);
  static const textFaint = Color(0xFF5E6474);
  static const signal = Color(0xFFE09261);
  static const owner = Color(0xFF7B9BE3);
  static const success = Color(0xFF65C5A0);
}

/// Text styles for the ambient palette. Colours come from [UiColors.of];
/// families are IntoChat's bundled fonts for the intochat palette and the v1
/// system fonts for midnight, paper and lumen.
@immutable
final class UiTypeSet {
  const UiTypeSet({
    required this.title,
    required this.heading,
    required this.cardTitle,
    required this.metric,
    required this.body,
    required this.bodyMuted,
    required this.meta,
    required this.metaStrong,
    required this.empty,
  });

  final TextStyle title;
  final TextStyle heading;
  final TextStyle cardTitle;
  final TextStyle metric;
  final TextStyle body;
  final TextStyle bodyMuted;
  final TextStyle meta;
  final TextStyle metaStrong;
  final TextStyle empty;
}

/// The static styles below are deprecated aliases of the v1 midnight set;
/// use [UiType.of]. They stay until every caller migrates (Task 5).
abstract final class UiType {
  static const fontPackage = 'intochat_os_ui';
  static const sansFamily = 'Schibsted Grotesk';
  static const intochatMonoFamily = 'JetBrains Mono';

  /// Package-qualified family names usable from any package.
  static const sans = 'packages/$fontPackage/$sansFamily';
  static const mono = 'packages/$fontPackage/$intochatMonoFamily';

  static UiTypeSet of(BuildContext context) {
    final c = UiColors.of(context);
    final intochat = c == UiColors.intochatLight || c == UiColors.intochatDark;
    TextStyle s(
      TextStyle v1,
      Color color, {
      bool isMono = false,
      bool head = false,
    }) {
      final style = v1.copyWith(
        fontFamily: intochat ? (isMono ? mono : sans) : null,
        color: color,
      );
      if (!intochat || !head) return style;
      return style.copyWith(
        fontWeight: FontWeight.w800,
        letterSpacing: -0.015 * (style.fontSize ?? 16),
        height: 1.18,
      );
    }

    return UiTypeSet(
      title: s(title, c.strong),
      heading: s(heading, c.strong, head: true),
      cardTitle: s(cardTitle, c.strong),
      metric: s(metric, c.strong),
      body: s(body, c.body),
      bodyMuted: s(bodyMuted, c.muted),
      meta: s(meta, c.muted, isMono: true),
      metaStrong: s(metaStrong, c.strong, isMono: true),
      empty: s(empty, c.strong, head: true),
    );
  }

  static const monoFamily = 'Cascadia Mono';
  static const monoFallback = ['Consolas', 'Menlo', 'monospace'];

  static const bodyFamily = 'Segoe UI Variable Text';
  static const bodyFallback = ['Segoe UI', 'Inter', 'Roboto'];

  static const title = TextStyle(
    fontFamily: bodyFamily,
    fontFamilyFallback: bodyFallback,
    fontSize: 15,
    fontWeight: FontWeight.w600,
    letterSpacing: -0.2,
    color: UiPalette.textPrimary,
  );

  static const heading = TextStyle(
    fontFamily: bodyFamily,
    fontFamilyFallback: bodyFallback,
    fontSize: 28,
    fontWeight: FontWeight.w600,
    letterSpacing: -0.8,
    color: UiPalette.textPrimary,
  );

  static const cardTitle = TextStyle(
    fontFamily: bodyFamily,
    fontFamilyFallback: bodyFallback,
    fontSize: 15,
    fontWeight: FontWeight.w600,
    letterSpacing: -0.15,
    color: UiPalette.textPrimary,
  );

  static const metric = TextStyle(
    fontFamily: bodyFamily,
    fontFamilyFallback: bodyFallback,
    fontSize: 17,
    fontWeight: FontWeight.w600,
    letterSpacing: -0.2,
    color: UiPalette.textPrimary,
  );

  static const body = TextStyle(
    fontFamily: bodyFamily,
    fontFamilyFallback: bodyFallback,
    fontSize: 15,
    height: 1.55,
    color: UiPalette.textPrimary,
  );

  static const bodyMuted = TextStyle(
    fontFamily: bodyFamily,
    fontFamilyFallback: bodyFallback,
    fontSize: 14,
    height: 1.5,
    color: UiPalette.textMuted,
  );

  static const meta = TextStyle(
    fontFamily: monoFamily,
    fontFamilyFallback: monoFallback,
    fontSize: 11,
    fontWeight: FontWeight.w500,
    letterSpacing: 0.45,
    color: UiPalette.textMuted,
  );

  static const metaStrong = TextStyle(
    fontFamily: monoFamily,
    fontFamilyFallback: monoFallback,
    fontSize: 11,
    fontWeight: FontWeight.w600,
    letterSpacing: 0.45,
    color: UiPalette.textPrimary,
  );

  static const empty = TextStyle(
    fontFamily: bodyFamily,
    fontFamilyFallback: bodyFallback,
    fontSize: 18,
    fontWeight: FontWeight.w600,
    letterSpacing: -0.3,
    color: UiPalette.textPrimary,
  );
}

abstract final class UiTheme {
  static const palettes = ['intochat', 'midnight', 'paper', 'lumen'];

  static String paletteName(String? stored) => switch (stored) {
    'dark' || 'midnight' => 'midnight',
    'light' || 'paper' => 'paper',
    'lumen' => 'lumen',
    _ => 'intochat',
  };

  static ThemeMode modeFor(String? stored) => switch (paletteName(stored)) {
    'intochat' => ThemeMode.system,
    'paper' => ThemeMode.light,
    _ => ThemeMode.dark,
  };

  /// Light theme for a stored palette. v1 palettes reuse their unchanged
  /// code path and only gain their [UiColors].
  static ThemeData light(String? stored) => switch (paletteName(stored)) {
    'intochat' => intochat(Brightness.light),
    _ => _withColors(workspace(Brightness.light), UiColors.paper),
  };

  /// Dark theme for a stored palette.
  static ThemeData dark(String? stored) => switch (paletteName(stored)) {
    'intochat' => intochat(Brightness.dark),
    'lumen' => _withColors(workspaceDark(stored), UiColors.lumen),
    _ => _withColors(workspaceDark(stored), UiColors.midnight),
  };

  static ThemeData _withColors(ThemeData base, UiColors colors) =>
      base.copyWith(
        extensions: [
          ...base.extensions.values.where((e) => e is! UiColors),
          colors,
        ],
      );

  /// IntoChat (Figma) theme: Schibsted Grotesk on warm neutrals, blue accent.
  static ThemeData intochat(Brightness brightness) {
    final c = brightness == Brightness.dark
        ? UiColors.intochatDark
        : UiColors.intochatLight;
    final scheme = ColorScheme(
      brightness: brightness,
      primary: c.accent,
      onPrimary: c.onAccent,
      primaryContainer: c.accentTint,
      onPrimaryContainer: c.strong,
      secondary: c.accent,
      onSecondary: c.onAccent,
      error: c.error,
      onError: c.onAccent,
      errorContainer: c.errorBg,
      onErrorContainer: c.error,
      surface: c.surface,
      onSurface: c.body,
      onSurfaceVariant: c.muted,
      surfaceContainerLowest: c.surface,
      surfaceContainerLow: c.surface,
      surfaceContainer: c.recessed,
      surfaceContainerHigh: c.recessed,
      surfaceContainerHighest: c.recessed,
      outline: c.inputBorder,
      outlineVariant: c.hairline,
    );
    final base = ThemeData(
      useMaterial3: true,
      brightness: brightness,
      fontFamily: UiType.sans,
    ).textTheme.apply(bodyColor: c.body, displayColor: c.strong);
    TextStyle? head(TextStyle? t) => t?.copyWith(
      fontWeight: FontWeight.w800,
      letterSpacing: -0.015 * (t.fontSize ?? 16),
      height: 1.18,
    );
    final textTheme = base.copyWith(
      displayLarge: head(base.displayLarge),
      displayMedium: head(base.displayMedium),
      displaySmall: head(base.displaySmall),
      headlineLarge: head(base.headlineLarge),
      headlineMedium: head(base.headlineMedium),
      headlineSmall: head(base.headlineSmall),
      bodyLarge: base.bodyLarge?.copyWith(fontSize: 16, height: 1.5),
      bodyMedium: base.bodyMedium?.copyWith(height: 1.5),
    );
    final border = OutlineInputBorder(
      borderRadius: BorderRadius.circular(10),
      borderSide: BorderSide(color: c.inputBorder),
    );
    return ThemeData(
      useMaterial3: true,
      brightness: brightness,
      extensions: [const UiTokens(), c],
      colorScheme: scheme,
      fontFamily: UiType.sans,
      scaffoldBackgroundColor: c.page,
      canvasColor: c.page,
      dividerColor: c.hairline,
      textTheme: textTheme,
      appBarTheme: AppBarTheme(
        backgroundColor: c.page,
        foregroundColor: c.strong,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        scrolledUnderElevation: 0,
        shape: Border(bottom: BorderSide(color: c.hairline)),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          minimumSize: const Size(48, 44),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(10),
          ),
          textStyle: const TextStyle(
            fontFamily: UiType.sans,
            fontSize: 14,
            fontWeight: FontWeight.w600,
          ),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: c.surface,
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 16,
          vertical: 16,
        ),
        border: border,
        enabledBorder: border,
        focusedBorder: border.copyWith(
          borderSide: BorderSide(color: c.accent, width: 2),
        ),
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: c.surface,
        surfaceTintColor: Colors.transparent,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(14),
          side: BorderSide(color: c.hairline),
        ),
      ),
      tooltipTheme: TooltipThemeData(
        decoration: BoxDecoration(
          color: c.strong,
          borderRadius: BorderRadius.circular(8),
        ),
        textStyle: TextStyle(
          fontFamily: UiType.sans,
          color: c.page,
          fontSize: 12,
        ),
      ),
    );
  }

  static ThemeData workspaceDark(String? stored) {
    final base = workspace(Brightness.dark);
    if (paletteName(stored) != 'lumen') return base;
    const background = Color(0xFF060A08);
    const surface = Color(0xFF0B120F);
    final scheme = base.colorScheme.copyWith(
      primary: const Color(0xFF7CE0A8),
      onPrimary: const Color(0xFF04170D),
      primaryContainer: const Color(0xFF12301F),
      onPrimaryContainer: const Color(0xFFA5F0C4),
      surface: surface,
      surfaceContainerLow: const Color(0xFF0F1813),
      surfaceContainer: const Color(0xFF131E18),
      surfaceContainerHigh: const Color(0xFF18261E),
      onSurface: const Color(0xFFE3EFE8),
      onSurfaceVariant: const Color(0xFF8CA397),
      outline: const Color(0xFF3F5A4A),
      outlineVariant: const Color(0xFF1D2D24),
    );
    return base.copyWith(
      colorScheme: scheme,
      scaffoldBackgroundColor: background,
      dividerColor: scheme.outlineVariant,
      textTheme: base.textTheme.apply(
        bodyColor: scheme.onSurface,
        displayColor: scheme.onSurface,
      ),
      appBarTheme: base.appBarTheme.copyWith(
        backgroundColor: background,
        foregroundColor: scheme.onSurface,
        shape: Border(bottom: BorderSide(color: scheme.outlineVariant)),
      ),
      inputDecorationTheme: base.inputDecorationTheme.copyWith(
        fillColor: scheme.surfaceContainerLow,
      ),
      dialogTheme: base.dialogTheme.copyWith(
        backgroundColor: scheme.surfaceContainerLow,
      ),
    );
  }

  /// Neutral workspace surfaces with the shared Lumen accent. Legacy graph and
  /// chat tokens remain independent while workspace pages migrate.
  static ThemeData workspace(Brightness brightness) {
    final darkMode = brightness == Brightness.dark;
    final base = darkMode ? baseDark() : baseLight();
    final scheme =
        ColorScheme.fromSeed(
          seedColor: LumenPalette.accent,
          brightness: brightness,
        ).copyWith(
          primary: darkMode ? const Color(0xFF91D3B8) : LumenPalette.accent,
          onPrimary: darkMode ? const Color(0xFF103B2D) : Colors.white,
          primaryContainer: darkMode
              ? const Color(0xFF243B34)
              : LumenPalette.accentSoft,
          onPrimaryContainer: darkMode
              ? const Color(0xFFA5DCC5)
              : LumenPalette.accent,
          surface: darkMode ? const Color(0xFF191A1B) : LumenPalette.surface,
          surfaceContainerLow: darkMode
              ? const Color(0xFF202122)
              : LumenPalette.surface,
          surfaceContainer: darkMode
              ? const Color(0xFF252627)
              : LumenPalette.surfaceMuted,
          surfaceContainerHigh: darkMode
              ? const Color(0xFF2B2C2D)
              : LumenPalette.surfaceMuted,
          onSurface: darkMode ? const Color(0xFFECEBE7) : LumenPalette.ink,
          onSurfaceVariant: darkMode
              ? const Color(0xFFA5A6A3)
              : LumenPalette.muted,
          outline: darkMode ? const Color(0xFF616360) : LumenPalette.lineStrong,
          outlineVariant: darkMode
              ? const Color(0xFF333533)
              : LumenPalette.line,
        );
    final border = OutlineInputBorder(
      borderRadius: BorderRadius.circular(10),
      borderSide: BorderSide(color: scheme.outline),
    );
    return base.copyWith(
      colorScheme: scheme,
      scaffoldBackgroundColor: darkMode
          ? const Color(0xFF171819)
          : LumenPalette.background,
      textTheme:
          ThemeData(
            brightness: brightness,
            fontFamily: UiType.bodyFamily,
            fontFamilyFallback: UiType.bodyFallback,
          ).textTheme.apply(
            bodyColor: scheme.onSurface,
            displayColor: scheme.onSurface,
          ),
      appBarTheme: AppBarTheme(
        backgroundColor: darkMode
            ? const Color(0xFF171819)
            : LumenPalette.background,
        foregroundColor: scheme.onSurface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        scrolledUnderElevation: 0,
        shape: Border(bottom: BorderSide(color: scheme.outlineVariant)),
      ),
      dividerColor: scheme.outlineVariant,
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          minimumSize: const Size(48, 44),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(10),
          ),
          textStyle: const TextStyle(
            fontFamily: UiType.bodyFamily,
            fontFamilyFallback: UiType.bodyFallback,
            fontSize: 14,
            fontWeight: FontWeight.w600,
          ),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: scheme.surfaceContainerLow,
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 16,
          vertical: 16,
        ),
        border: border,
        enabledBorder: border,
        focusedBorder: border.copyWith(
          borderSide: BorderSide(color: scheme.primary, width: 2),
        ),
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: scheme.surfaceContainerLow,
        surfaceTintColor: Colors.transparent,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(18),
          side: BorderSide(color: scheme.outlineVariant),
        ),
      ),
    );
  }

  /// v1 light base (formerly `light()`).
  static ThemeData baseLight() => ThemeData(
    useMaterial3: true,
    extensions: const [UiTokens()],
    brightness: Brightness.light,
    fontFamily: UiType.bodyFamily,
    fontFamilyFallback: UiType.bodyFallback,
    colorScheme: const ColorScheme.light(
      primary: LumenPalette.accent,
      onPrimary: LumenPalette.surface,
      secondary: LumenPalette.accentSoft,
      onSecondary: LumenPalette.ink,
      surface: LumenPalette.surface,
      onSurface: LumenPalette.ink,
      error: LumenPalette.error,
      outline: LumenPalette.lineStrong,
      outlineVariant: LumenPalette.line,
    ),
    scaffoldBackgroundColor: LumenPalette.background,
    dividerColor: LumenPalette.line,
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: LumenPalette.surface,
      border: OutlineInputBorder(
        borderRadius: BorderRadius.circular(12),
        borderSide: const BorderSide(color: LumenPalette.line),
      ),
    ),
    tooltipTheme: TooltipThemeData(
      decoration: BoxDecoration(
        color: LumenPalette.ink,
        borderRadius: BorderRadius.circular(8),
      ),
      textStyle: const TextStyle(color: LumenPalette.surface, fontSize: 12),
    ),
  );

  /// v1 dark base (formerly `dark()`).
  static ThemeData baseDark() {
    final scheme = ColorScheme.fromSeed(
      seedColor: UiPalette.signal,
      brightness: Brightness.dark,
      surface: UiPalette.surface,
    );

    return ThemeData(
      useMaterial3: true,
      extensions: const [UiTokens()],
      brightness: Brightness.dark,
      scaffoldBackgroundColor: UiPalette.surface,
      colorScheme: scheme.copyWith(
        primary: UiPalette.signal,
        secondary: UiPalette.owner,
        surface: UiPalette.surface,
      ),
      dividerColor: UiPalette.line,
      navigationRailTheme: NavigationRailThemeData(
        backgroundColor: UiPalette.navigation,
        indicatorColor: UiPalette.signal.withValues(alpha: 0.14),
        selectedIconTheme: const IconThemeData(
          color: UiPalette.signal,
          size: 21,
        ),
        unselectedIconTheme: const IconThemeData(
          color: UiPalette.textMuted,
          size: 20,
        ),
        selectedLabelTextStyle: UiType.metaStrong.copyWith(
          color: UiPalette.signal,
        ),
        unselectedLabelTextStyle: UiType.meta,
      ),
      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: UiPalette.navigation,
        indicatorColor: UiPalette.signal.withValues(alpha: 0.14),
        labelTextStyle: WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected)
              ? UiType.metaStrong.copyWith(color: UiPalette.signal)
              : UiType.meta,
        ),
      ),
      tooltipTheme: const TooltipThemeData(
        decoration: BoxDecoration(color: UiPalette.surfaceRaised),
        textStyle: UiType.meta,
      ),
    );
  }
}

/// Installs the approved product control foundation below MaterialApp.
///
/// Localization stays in this bridge so surfaces never need to import Forui.
final class UiThemeScope extends StatelessWidget {
  const UiThemeScope({super.key, required this.child, this.brightness});

  final Widget child;
  final Brightness? brightness;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colors = theme.colorScheme;
    final data = FThemeData(
      debugLabel: 'IntoChat.OS',
      touch: true,
      colors:
          (theme.brightness == Brightness.light
                  ? FColors.neutralLight
                  : FColors.neutralDark)
              .copyWith(
                background: theme.scaffoldBackgroundColor,
                foreground: colors.onSurface,
                primary: colors.primary,
                primaryForeground: colors.onPrimary,
                secondary: colors.primaryContainer,
                secondaryForeground: colors.onPrimaryContainer,
                muted: colors.surfaceContainer,
                mutedForeground: colors.onSurfaceVariant,
                card: colors.surface,
                border: colors.outlineVariant,
                destructive: colors.error,
                error: colors.error,
              ),
    );
    return Localizations.override(
      context: context,
      delegates: FLocalizations.localizationsDelegates,
      child: material.Theme(
        data: data.toApproximateMaterialTheme(),
        child: material.Material(
          type: material.MaterialType.transparency,
          child: FTheme(data: data, child: child),
        ),
      ),
    );
  }
}

abstract final class UiChatTheme {
  static ChatTheme workspace(ThemeData theme) {
    final c = theme.colorScheme;
    return ChatTheme(
      colors: ChatColors(
        primary: c.primary,
        onPrimary: c.onPrimary,
        surface: theme.scaffoldBackgroundColor,
        onSurface: c.onSurface,
        surfaceContainer: c.surfaceContainer,
        surfaceContainerLow: c.surfaceContainerLow,
        surfaceContainerHigh: c.surfaceContainerHigh,
      ),
      typography: ChatTypography.standard(fontFamily: UiType.bodyFamily)
          .copyWith(
            bodyMedium: (theme.textTheme.bodyMedium ?? UiType.body).copyWith(
              fontSize: 15,
              height: 1.65,
              color: c.onSurface,
            ),
            bodySmall: (theme.textTheme.bodySmall ?? UiType.bodyMuted).copyWith(
              fontSize: 12,
              color: c.onSurfaceVariant,
            ),
            labelSmall: (theme.textTheme.labelSmall ?? UiType.meta).copyWith(
              fontSize: 11,
              color: c.onSurfaceVariant,
            ),
          ),
      shape: BorderRadius.circular(12),
    );
  }

  static ChatTheme light() => ChatTheme(
    colors: const ChatColors(
      primary: LumenPalette.accent,
      onPrimary: LumenPalette.surface,
      surface: LumenPalette.background,
      onSurface: LumenPalette.ink,
      surfaceContainer: LumenPalette.surface,
      surfaceContainerLow: LumenPalette.surfaceMuted,
      surfaceContainerHigh: LumenPalette.accentSoft,
    ),
    typography: ChatTypography.standard(fontFamily: UiType.bodyFamily).copyWith(
      bodyMedium: UiType.body.copyWith(color: LumenPalette.ink),
      bodySmall: UiType.bodyMuted.copyWith(color: LumenPalette.muted),
      labelSmall: UiType.meta.copyWith(color: LumenPalette.muted),
    ),
    shape: BorderRadius.circular(16),
  );

  static ChatTheme dark() {
    final base = ChatTypography.standard(fontFamily: UiType.bodyFamily);
    return ChatTheme(
      colors: const ChatColors(
        primary: UiPalette.owner,
        onPrimary: UiPalette.surface,
        surface: UiPalette.surface,
        onSurface: UiPalette.textPrimary,
        surfaceContainer: UiPalette.surfaceRaised,
        surfaceContainerLow: UiPalette.surfaceSunken,
        surfaceContainerHigh: UiPalette.line,
      ),
      typography: base.copyWith(
        bodyMedium: UiType.body,
        bodySmall: UiType.bodyMuted,
        labelSmall: UiType.meta,
      ),
      shape: BorderRadius.circular(14),
    );
  }
}
