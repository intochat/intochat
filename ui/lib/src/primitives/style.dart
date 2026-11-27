import 'package:flutter/material.dart';
import 'package:mix/mix.dart';

import '../theme/mix_tokens.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'ui_control_surface.dart';

/// Optional state-specific style overrides mirroring C# `Mix.Style()`
/// `hover`, `press`, and `focus` blocks.
@immutable
final class UiStyleStateOverride {
  const UiStyleStateOverride({
    this.tone,
    this.typography,
    this.elevation,
    this.icon,
    this.motion,
    this.paddingX,
    this.paddingY,
    this.gap,
    this.radius,
    this.scale,
  });

  final String? tone;
  final String? typography;
  final String? elevation;
  final String? icon;
  final UiMotionPreset? motion;
  final double? paddingX;
  final double? paddingY;
  final double? gap;
  final double? radius;
  final double? scale;

  static UiStyleStateOverride? fromJson(Object? raw) {
    if (raw is! Map) return null;
    return UiStyleStateOverride(
      tone: raw['tone'] != null ? '${raw['tone']}'.trim().toLowerCase() : null,
      typography: raw['typography'] != null
          ? '${raw['typography']}'.trim().toLowerCase()
          : null,
      elevation: raw['elevation'] != null
          ? '${raw['elevation']}'.trim().toLowerCase()
          : null,
      icon: raw['icon'] as String?,
      motion: UiStyleData._parseMotionNullable(raw['motion']),
      paddingX: ((raw['paddingX'] ?? raw['px']) as num?)?.toDouble(),
      paddingY: ((raw['paddingY'] ?? raw['py']) as num?)?.toDouble(),
      gap: (raw['gap'] as num?)?.toDouble(),
      radius: (raw['radius'] as num?)?.toDouble(),
      scale: (raw['scale'] as num?)?.toDouble(),
    );
  }

  @override
  bool operator ==(Object other) =>
      other is UiStyleStateOverride &&
      other.tone == tone &&
      other.typography == typography &&
      other.elevation == elevation &&
      other.icon == icon &&
      other.motion == motion &&
      other.paddingX == paddingX &&
      other.paddingY == paddingY &&
      other.gap == gap &&
      other.radius == radius &&
      other.scale == scale;

  @override
  int get hashCode => Object.hash(
    tone,
    typography,
    elevation,
    icon,
    motion,
    paddingX,
    paddingY,
    gap,
    radius,
    scale,
  );
}

/// Style of a Mix primitive: tone, typography, elevation, padding, radius,
/// motion, and per-state overrides (`hover`, `press`, `focus`).
@immutable
final class UiStyleData {
  const UiStyleData({
    this.tone = 'default',
    this.typography = 'body',
    this.elevation = 'flat',
    this.icon,
    this.motion = UiMotionPreset.standard,
    this.paddingX,
    this.paddingY,
    this.gap,
    this.radius,
    this.scale,
    this.hover,
    this.press,
    this.focus,
  });

  final String tone;
  final String typography;
  final String elevation;
  final String? icon;
  final UiMotionPreset motion;
  final double? paddingX;
  final double? paddingY;
  final double? gap;
  final double? radius;
  final double? scale;
  final UiStyleStateOverride? hover;
  final UiStyleStateOverride? press;
  final UiStyleStateOverride? focus;

  bool get hasCustomTone => tone != 'default';
  bool get hasCustomElevation => elevation != 'flat';

  factory UiStyleData.fromJson(Map<String, dynamic>? json) {
    if (json == null) return const UiStyleData();
    final rawTone =
        json['tone'] ?? _presetToTone(json['variant'] ?? json['preset']);
    return UiStyleData(
      tone: '${rawTone ?? 'default'}'.trim().toLowerCase(),
      typography: '${json['typography'] ?? 'body'}'.trim().toLowerCase(),
      elevation: '${json['elevation'] ?? 'flat'}'.trim().toLowerCase(),
      icon: json['icon'] as String?,
      motion: _parseMotion(json['motion']),
      paddingX: ((json['paddingX'] ?? json['px']) as num?)?.toDouble(),
      paddingY: ((json['paddingY'] ?? json['py']) as num?)?.toDouble(),
      gap: (json['gap'] as num?)?.toDouble(),
      radius: (json['radius'] as num?)?.toDouble(),
      scale: (json['scale'] as num?)?.toDouble(),
      hover: UiStyleStateOverride.fromJson(json['hover']),
      press: UiStyleStateOverride.fromJson(json['press']),
      focus: UiStyleStateOverride.fromJson(json['focus']),
    );
  }

  /// Parses a wire `style` value that may be either a structured C#
  /// `Mix.Style()` JSON object (`Map`) or a legacy string preset (`"primary"`).
  static UiStyleData? fromWire(Object? raw) {
    if (raw is Map<String, dynamic>) {
      return UiStyleData.fromJson(raw);
    }
    if (raw is Map) {
      return UiStyleData.fromJson(
        raw.map((key, value) => MapEntry('$key', value)),
      );
    }
    if (raw is String && raw.trim().isNotEmpty) {
      return UiStyleData.fromPreset(raw);
    }
    return null;
  }

  /// Creates a [UiStyleData] from a legacy string preset name (`primary`,
  /// `secondary`, `ghost`, `danger`, `link`).
  factory UiStyleData.fromPreset(String preset) {
    final normalized = preset.trim().toLowerCase();
    return switch (normalized) {
      'primary' => const UiStyleData(
        tone: 'accent',
        hover: UiStyleStateOverride(tone: 'accentstrong'),
      ),
      'ghost' => const UiStyleData(
        tone: 'ghost',
        hover: UiStyleStateOverride(tone: 'subtle'),
      ),
      'danger' => const UiStyleData(tone: 'danger'),
      'link' => const UiStyleData(tone: 'link'),
      'secondary' || '' => const UiStyleData(
        tone: 'secondary',
        hover: UiStyleStateOverride(tone: 'subtle'),
      ),
      _ => UiStyleData(tone: normalized),
    };
  }

  /// Merges active [focus], [hover], and [press] state overrides (in increasing
  /// precedence) onto this base style.
  UiStyleData resolve({
    bool hovered = false,
    bool pressed = false,
    bool focused = false,
  }) {
    final focusOv = focused ? focus : null;
    final hoverOv = hovered ? hover : null;
    final pressOv = pressed ? press : null;
    if (focusOv == null && hoverOv == null && pressOv == null) return this;

    return UiStyleData(
      tone: pressOv?.tone ?? hoverOv?.tone ?? focusOv?.tone ?? tone,
      typography:
          pressOv?.typography ??
          hoverOv?.typography ??
          focusOv?.typography ??
          typography,
      elevation:
          pressOv?.elevation ??
          hoverOv?.elevation ??
          focusOv?.elevation ??
          elevation,
      icon: pressOv?.icon ?? hoverOv?.icon ?? focusOv?.icon ?? icon,
      motion: pressOv?.motion ?? hoverOv?.motion ?? focusOv?.motion ?? motion,
      paddingX:
          pressOv?.paddingX ??
          hoverOv?.paddingX ??
          focusOv?.paddingX ??
          paddingX,
      paddingY:
          pressOv?.paddingY ??
          hoverOv?.paddingY ??
          focusOv?.paddingY ??
          paddingY,
      gap: pressOv?.gap ?? hoverOv?.gap ?? focusOv?.gap ?? gap,
      radius: pressOv?.radius ?? hoverOv?.radius ?? focusOv?.radius ?? radius,
      scale: pressOv?.scale ?? hoverOv?.scale ?? focusOv?.scale ?? scale,
      hover: hover,
      press: press,
      focus: focus,
    );
  }

  static String? _presetToTone(Object? raw) {
    if (raw is! String || raw.trim().isEmpty) return null;
    return switch (raw.trim().toLowerCase()) {
      'primary' => 'accent',
      'secondary' => 'secondary',
      'ghost' => 'ghost',
      'danger' => 'danger',
      'link' => 'link',
      final other => other,
    };
  }

  static UiMotionPreset _parseMotion(Object? raw) =>
      _parseMotionNullable(raw) ?? UiMotionPreset.standard;

  static UiMotionPreset? _parseMotionNullable(Object? raw) {
    if (raw == null) return null;
    final name = '$raw'.trim().toLowerCase();
    for (final preset in UiMotionPreset.values) {
      if (preset.name == name) return preset;
    }
    return null;
  }

  @override
  bool operator ==(Object other) =>
      other is UiStyleData &&
      other.tone == tone &&
      other.typography == typography &&
      other.elevation == elevation &&
      other.icon == icon &&
      other.motion == motion &&
      other.paddingX == paddingX &&
      other.paddingY == paddingY &&
      other.gap == gap &&
      other.radius == radius &&
      other.scale == scale &&
      other.hover == hover &&
      other.press == press &&
      other.focus == focus;

  @override
  int get hashCode => Object.hash(
    tone,
    typography,
    elevation,
    icon,
    motion,
    paddingX,
    paddingY,
    gap,
    radius,
    scale,
    hover,
    press,
    focus,
  );
}

/// Single authoritative mapper converting C# [UiStyleData] into Flutter Mix
/// [BoxStyler], shadows, scale, and Inter [TextStyle] so C#-authored padding,
/// radius, tone, typography, elevation, motion, and state overrides render
/// with 1-to-1 parity across every UI control.
abstract final class UiStyleMapper {
  static Color resolveToneBackground(UiColors colors, String tone) =>
      switch (tone.trim().toLowerCase()) {
        'accent' || 'primary' => colors.accent,
        'accentstrong' || 'accent-strong' => colors.accentHover,
        'accenttint' || 'accent-tint' => colors.accentTint,
        'success' || 'done' => colors.done,
        'warning' || 'working' => colors.working,
        'danger' || 'error' => colors.error,
        'subtle' || 'recessed' => colors.recessed,
        'elevated' => colors.elevated,
        'ghost' || 'link' || 'transparent' => Colors.transparent,
        'card' || 'secondary' => colors.surface,
        'canvas' => colors.page,
        _ => colors.surface,
      };

  static Color _resolveToneBackgroundFromContext(
    BuildContext context,
    UiColors colors,
    String tone,
  ) => switch (tone.trim().toLowerCase()) {
    'accent' || 'primary' => UiMixTokens.resolveOr(
      context,
      UiMixTokens.accent,
      colors.accent,
    ),
    'accentstrong' || 'accent-strong' => UiMixTokens.resolveOr(
      context,
      UiMixTokens.accentHover,
      colors.accentHover,
    ),
    'accenttint' || 'accent-tint' => UiMixTokens.resolveOr(
      context,
      UiMixTokens.accentTint,
      colors.accentTint,
    ),
    'success' ||
    'done' => UiMixTokens.resolveOr(context, UiMixTokens.done, colors.done),
    'warning' || 'working' => UiMixTokens.resolveOr(
      context,
      UiMixTokens.working,
      colors.working,
    ),
    'danger' ||
    'error' => UiMixTokens.resolveOr(context, UiMixTokens.error, colors.error),
    'subtle' || 'recessed' => UiMixTokens.resolveOr(
      context,
      UiMixTokens.recessed,
      colors.recessed,
    ),
    'elevated' => UiMixTokens.resolveOr(
      context,
      UiMixTokens.elevated,
      colors.elevated,
    ),
    'ghost' || 'link' || 'transparent' => Colors.transparent,
    'canvas' => UiMixTokens.resolveOr(context, UiMixTokens.page, colors.page),
    _ => UiMixTokens.resolveOr(context, UiMixTokens.surface, colors.surface),
  };

  static Color resolveToneForeground(UiColors colors, String tone) =>
      switch (tone.trim().toLowerCase()) {
        'accent' ||
        'primary' ||
        'accentstrong' ||
        'accent-strong' => colors.onAccent,
        'success' ||
        'done' ||
        'warning' ||
        'working' ||
        'danger' ||
        'error' => Colors.white,
        'accenttint' || 'accent-tint' || 'link' => colors.accentCopy,
        'ghost' => colors.body,
        _ => colors.strong,
      };

  static List<BoxShadow> elevationShadows(UiColors colors, String elevation) =>
      switch (elevation.trim().toLowerCase()) {
        'subtle' || 'xs' => UiShadows.xs(colors),
        'raised' || 'sm' => UiShadows.sm(colors),
        'floating' || 'md' => UiShadows.md(colors),
        'overlay' || 'lg' => UiShadows.lg(colors),
        _ => const <BoxShadow>[],
      };

  static bool _isNeutralTone(String tone) =>
      switch (tone.trim().toLowerCase()) {
        'accent' ||
        'primary' ||
        'accentstrong' ||
        'accent-strong' ||
        'accenttint' ||
        'accent-tint' ||
        'link' ||
        'success' ||
        'done' ||
        'warning' ||
        'working' ||
        'danger' ||
        'error' => false,
        _ => true,
      };

  static TextStyle textStyle(
    BuildContext context,
    UiStyleData style, {
    bool isEnabled = true,
    Color? colorOverride,
  }) {
    final colors = UiColors.of(context);
    final baseFg = colorOverride ?? resolveToneForeground(colors, style.tone);
    final fg = isEnabled ? baseFg : baseFg.withValues(alpha: 0.5);
    final bodyBaseFg =
        colorOverride ??
        (_isNeutralTone(style.tone)
            ? colors.textBody
            : resolveToneForeground(colors, style.tone));
    final bodyFg = isEnabled ? bodyBaseFg : bodyBaseFg.withValues(alpha: 0.5);
    final metaBaseFg = colorOverride ?? colors.textMuted;
    final metaFg = isEnabled ? metaBaseFg : metaBaseFg.withValues(alpha: 0.5);

    return switch (style.typography.trim().toLowerCase()) {
      'display' => UiType.displayStyle(24, weight: FontWeight.w700, color: fg),
      'heading' ||
      'title' => UiType.displayStyle(17, weight: FontWeight.w700, color: fg),
      'label' => UiType.textStyle(13.5, weight: FontWeight.w600, color: fg),
      'body' => UiType.textStyle(
        14,
        weight: FontWeight.w400,
        color: bodyFg,
        height: 1.5,
      ),
      'meta' || 'secondary' => UiType.textStyle(
        13,
        weight: FontWeight.w400,
        color: metaFg,
      ),
      'caption' => UiType.textStyle(12.5, weight: FontWeight.w400, color: fg),
      'group' => UiType.textStyle(
        11.5,
        weight: FontWeight.w700,
        color: fg,
        letterSpacing: 0.6,
      ),
      'metric' => UiType.textStyle(
        16,
        weight: FontWeight.w600,
        color: fg,
        tnum: true,
      ),
      'tabular' ||
      'code' => UiType.tabularStyle(13, weight: FontWeight.w500, color: fg),
      _ => UiType.textStyle(14, weight: FontWeight.w400, color: fg),
    };
  }

  static double resolveScale(
    UiStyleData style, {
    bool isHovered = false,
    bool isPressed = false,
    bool isFocused = false,
    bool isEnabled = true,
  }) {
    if (!isEnabled) return 1.0;
    final resolved = style.resolve(
      hovered: isHovered,
      pressed: isPressed,
      focused: isFocused,
    );
    return resolved.scale ?? (isPressed ? 0.97 : 1.0);
  }

  static BoxStyler boxStyler(
    BuildContext context,
    UiStyleData style, {
    bool isHovered = false,
    bool isPressed = false,
    bool isFocused = false,
    bool isEnabled = true,
  }) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);

    final activeHover = isHovered && isEnabled;
    final activePress = isPressed && isEnabled;
    final activeFocus = isFocused && isEnabled;

    final resolved = style.resolve(
      hovered: activeHover,
      pressed: activePress,
      focused: activeFocus,
    );

    final hasExplicitPressTone = activePress && style.press?.tone != null;
    final hasExplicitHoverTone = activeHover && style.hover?.tone != null;

    final baseBg = _resolveToneBackgroundFromContext(
      context,
      colors,
      resolved.tone,
    );
    final recessed = UiMixTokens.resolveOr(
      context,
      UiMixTokens.recessed,
      colors.recessed,
    );
    final Color effectiveBg;
    if (!isEnabled) {
      effectiveBg = baseBg == Colors.transparent
          ? Colors.transparent
          : baseBg.withValues(alpha: 0.45);
    } else if (activePress && !hasExplicitPressTone) {
      effectiveBg = baseBg == Colors.transparent
          ? recessed
          : baseBg.withValues(alpha: 0.82);
    } else if (activeHover && !hasExplicitHoverTone) {
      effectiveBg = baseBg == Colors.transparent
          ? recessed.withValues(alpha: 0.7)
          : baseBg.withValues(alpha: 0.92);
    } else {
      effectiveBg = baseBg;
    }

    final px =
        resolved.paddingX ??
        UiMixTokens.resolveOr(
          context,
          UiMixTokens.spaceMedium,
          tokens.spaceMedium,
        );
    final py =
        resolved.paddingY ??
        UiMixTokens.resolveOr(
          context,
          UiMixTokens.spaceSmall,
          tokens.spaceSmall,
        );
    final r =
        resolved.radius ??
        UiMixTokens.resolveOr(
          context,
          UiMixTokens.controlRadius,
          Radius.circular(tokens.controlRadius),
        ).x;

    final isGhostOrLink = resolved.tone == 'ghost' || resolved.tone == 'link';
    final borderColor = activeFocus
        ? UiMixTokens.resolveOr(context, UiMixTokens.focus, colors.focus)
        : activeHover
        ? UiMixTokens.resolveOr(context, UiMixTokens.accent, colors.accent)
        : isGhostOrLink
        ? Colors.transparent
        : UiMixTokens.resolveOr(context, UiMixTokens.hairline, colors.hairline);

    var styler = BoxStyler()
        .color(effectiveBg)
        .padding(EdgeInsetsGeometryMix.symmetric(horizontal: px, vertical: py))
        .borderRadius(BorderRadiusGeometryMix.circular(r))
        .border(
          BoxBorderMix.all(
            BorderSideMix.color(borderColor).width(activeFocus ? 1.5 : 1.0),
          ),
        )
        .animate(
          AnimationConfig.curve(
            duration: resolved.motion.duration,
            curve: resolved.motion.curve,
          ),
        );

    final shadows = elevationShadows(colors, resolved.elevation);
    if (shadows.isNotEmpty) {
      styler = styler.shadows([
        for (final s in shadows)
          BoxShadowMix(
            color: s.color,
            offset: s.offset,
            blurRadius: s.blurRadius,
            spreadRadius: s.spreadRadius,
          ),
      ]);
    }

    return styler;
  }

  /// Builds a declarative `mix` [BoxStyler] with native `.onFocused`,
  /// `.onHovered`, `.onPressed`, and `.onDisabled` widget-state variants for
  /// `PressableBox` and `WidgetState`-driven containers.
  static BoxStyler widgetStateBoxStyler(
    BuildContext context,
    UiStyleData style,
  ) {
    final base = boxStyler(context, style);
    final focused = boxStyler(context, style, isFocused: true);
    final hovered = boxStyler(context, style, isHovered: true);
    final pressed = boxStyler(context, style, isPressed: true);
    final disabled = boxStyler(context, style, isEnabled: false);

    return base
        .onFocused(focused)
        .onHovered(hovered)
        .onPressed(pressed)
        .onDisabled(disabled);
  }
}
