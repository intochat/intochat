import 'dart:async';

import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_tokens.dart';
import '../ui_control_surface.dart';

/// Mirrors C# `IntoChat.OS.Modules.UI.Core.UiStyleRef` 1-to-1.
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
    );
  }

  static UiMotionPreset _parseMotion(Object? raw) {
    final name = '$raw'.toLowerCase();
    for (final preset in UiMotionPreset.values) {
      if (preset.name == name) return preset;
    }
    return UiMotionPreset.standard;
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
/// C# [UiStyleData], with opt-in WinForms-style [UiEventMask] signal dispatch.
final class UiMixButton extends StatefulWidget {
  const UiMixButton({
    super.key,
    required this.label,
    this.onPressed,
    this.style = const UiStyleData(tone: 'accent', paddingX: 16, paddingY: 10),
    this.enabled = true,
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
  });

  final String label;
  final VoidCallback? onPressed;
  final UiStyleData style;
  final bool enabled;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;

  @override
  State<UiMixButton> createState() => _UiMixButtonState();
}

class _UiMixButtonState extends State<UiMixButton> {
  bool _hovered = false;
  bool _pressed = false;
  bool _focused = false;

  void _emit(int flag, String method, List<Object?> args) {
    if (!widget.enabled || (widget.eventMask & flag) == 0) return;
    final emitter = widget.onEmit;
    if (emitter != null) unawaited(emitter(method, args));
  }

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final fg = UiStyleMapper.resolveToneForeground(colors, widget.style.tone);

    final button = NakedButton(
      enabled: widget.enabled,
      onPressed: widget.onPressed,
      onHoverChange: (hovered) {
        if (_hovered == hovered) return;
        setState(() => _hovered = hovered);
        _emit(
          UiEventMask.hover,
          hovered ? 'OnPointerEnter' : 'OnPointerLeave',
          const [{}],
        );
      },
      onPressChange: (pressed) {
        if (_pressed == pressed) return;
        setState(() => _pressed = pressed);
        _emit(
          UiEventMask.press,
          pressed ? 'OnPointerDown' : 'OnPointerUp',
          const [{}],
        );
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
        _emit(UiEventMask.focus, 'OnFocusChanged', [focused]);
      },
      child: AnimatedScale(
        scale: _pressed && widget.enabled ? 0.97 : 1.0,
        duration: widget.style.motion.duration,
        curve: widget.style.motion.curve,
        child: Box(
          style: UiStyleMapper.boxStyler(
            context,
            widget.style,
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
