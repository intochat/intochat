import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
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
    if (raw is! Map<String, dynamic>) return null;
    return UiStyleStateOverride(
      tone: raw['tone'] != null ? '${raw['tone']}'.toLowerCase() : null,
      typography: raw['typography'] != null
          ? '${raw['typography']}'.toLowerCase()
          : null,
      elevation: raw['elevation'] != null
          ? '${raw['elevation']}'.toLowerCase()
          : null,
      icon: raw['icon'] as String?,
      motion: UiStyleData._parseMotionNullable(raw['motion']),
      paddingX: (raw['paddingX'] as num?)?.toDouble(),
      paddingY: (raw['paddingY'] as num?)?.toDouble(),
      gap: (raw['gap'] as num?)?.toDouble(),
      radius: (raw['radius'] as num?)?.toDouble(),
      scale: (raw['scale'] as num?)?.toDouble(),
    );
  }
}

/// Style of a Mix primitive: tone, padding, radius, motion and per-state overrides.
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

  factory UiStyleData.fromJson(Map<String, dynamic>? json) {
    if (json == null) return const UiStyleData();
    return UiStyleData(
      tone: '${json['tone'] ?? 'default'}'.toLowerCase(),
      typography: '${json['typography'] ?? 'body'}'.toLowerCase(),
      elevation: '${json['elevation'] ?? 'flat'}'.toLowerCase(),
      icon: json['icon'] as String?,
      motion: _parseMotion(json['motion']),
      paddingX: (json['paddingX'] as num?)?.toDouble(),
      paddingY: (json['paddingY'] as num?)?.toDouble(),
      gap: (json['gap'] as num?)?.toDouble(),
      radius: (json['radius'] as num?)?.toDouble(),
      scale: (json['scale'] as num?)?.toDouble(),
      hover: UiStyleStateOverride.fromJson(json['hover']),
      press: UiStyleStateOverride.fromJson(json['press']),
      focus: UiStyleStateOverride.fromJson(json['focus']),
    );
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

  static UiMotionPreset _parseMotion(Object? raw) =>
      _parseMotionNullable(raw) ?? UiMotionPreset.standard;

  static UiMotionPreset? _parseMotionNullable(Object? raw) {
    if (raw == null) return null;
    final name = '$raw'.toLowerCase();
    for (final preset in UiMotionPreset.values) {
      if (preset.name == name) return preset;
    }
    return null;
  }
}

/// Single authoritative mapper converting C# [UiStyleData] into Flutter Mix
/// [BoxStyler] so C#-authored padding, radius, tone, elevation, and motion
/// render with 1-to-1 parity across every UI control.
abstract final class UiStyleMapper {
  static Color resolveToneBackground(UiColors colors, String tone) =>
      switch (tone) {
        'accent' => colors.accent,
        'success' => colors.done,
        'warning' => colors.working,
        'danger' => colors.error,
        'subtle' => colors.recessed,
        'card' => colors.surface,
        'canvas' => colors.page,
        _ => colors.surface,
      };

  static Color resolveToneForeground(UiColors colors, String tone) =>
      switch (tone) {
        'accent' || 'success' || 'warning' || 'danger' => Colors.white,
        _ => colors.strong,
      };

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

    final baseBg = resolveToneBackground(colors, style.tone);
    final effectiveBg = !isEnabled
        ? baseBg.withValues(alpha: 0.45)
        : isPressed
        ? baseBg.withValues(alpha: 0.82)
        : isHovered
        ? baseBg.withValues(alpha: 0.92)
        : baseBg;

    final px = style.paddingX ?? tokens.spaceMedium;
    final py = style.paddingY ?? tokens.spaceSmall;
    final r = style.radius ?? tokens.controlRadius;

    final borderColor = isFocused
        ? colors.focus
        : isHovered
        ? colors.accent
        : colors.hairline;

    return BoxStyler()
        .color(effectiveBg)
        .padding(EdgeInsetsGeometryMix.symmetric(horizontal: px, vertical: py))
        .borderRadius(BorderRadiusGeometryMix.circular(r))
        .border(
          BoxBorderMix.all(
            BorderSideMix.color(borderColor).width(isFocused ? 1.5 : 1.0),
          ),
        )
        .animate(
          AnimationConfig.curve(
            duration: style.motion.duration,
            curve: style.motion.curve,
          ),
        );
  }
}

/// First Option B flagship control: headless `NakedButton` (accessibility,
/// keyboard focus, hover/press/focus states) + `mix` `Box` driven directly by
/// C# [UiStyleData].
final class UiMixButton extends StatefulWidget {
  const UiMixButton({
    super.key,
    required this.label,
    this.onPressed,
    this.style = const UiStyleData(tone: 'accent', paddingX: 16, paddingY: 10),
    this.enabled = true,
    this.semanticsId,
  });

  final String label;
  final VoidCallback? onPressed;
  final UiStyleData style;
  final bool enabled;
  final String? semanticsId;

  @override
  State<UiMixButton> createState() => _UiMixButtonState();
}

class _UiMixButtonState extends State<UiMixButton> {
  bool _hovered = false;
  bool _pressed = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final resolved = widget.style.resolve(
      hovered: _hovered,
      pressed: _pressed,
      focused: _focused,
    );
    final fg = UiStyleMapper.resolveToneForeground(colors, resolved.tone);

    final button = NakedButton(
      enabled: widget.enabled,
      onPressed: widget.onPressed,
      onHoverChange: (hovered) {
        if (_hovered == hovered) return;
        setState(() => _hovered = hovered);
      },
      onPressChange: (pressed) {
        if (_pressed == pressed) return;
        setState(() => _pressed = pressed);
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
      },
      child: AnimatedScale(
        scale: resolved.scale ?? (_pressed && widget.enabled ? 0.97 : 1.0),
        duration: resolved.motion.duration,
        curve: resolved.motion.curve,
        child: Box(
          style: UiStyleMapper.boxStyler(
            context,
            resolved,
            isHovered: _hovered,
            isPressed: _pressed,
            isFocused: _focused,
            isEnabled: widget.enabled,
          ),
          child: Text(
            widget.label,
            style: TextStyle(
              color: widget.enabled ? fg : fg.withValues(alpha: 0.5),
              fontWeight: FontWeight.w600,
              fontSize: 13.5,
            ),
          ),
        ),
      ),
    );

    if (widget.semanticsId == null) return button;
    return Semantics(
      identifier: widget.semanticsId,
      button: true,
      enabled: widget.enabled,
      child: button,
    );
  }
}
