import 'dart:async';

import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_tokens.dart';
import '../ui/ui_control_surface.dart';
import 'style.dart';

/// Option B `naked_ui` (`NakedCheckbox`) + `mix` Checkbox control with exact
/// C# [UiStyleData] parity, spring check reveal, and opt-in WinForms-style
/// [UiEventMask] signal dispatch.
final class UiMixCheckbox extends StatefulWidget {
  const UiMixCheckbox({
    super.key,
    required this.value,
    required this.onChanged,
    this.label = '',
    this.enabled = true,
    this.style = const UiStyleData(
      tone: 'accent',
      motion: UiMotionPreset.spring,
    ),
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
  });

  final bool value;
  final ValueChanged<bool>? onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;

  @override
  State<UiMixCheckbox> createState() => _UiMixCheckboxState();
}

class _UiMixCheckboxState extends State<UiMixCheckbox> {
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
    final tokens = UiTokens.of(context);
    final activeBg = UiStyleMapper.resolveToneBackground(
      colors,
      widget.style.tone,
    );
    final fillColor = widget.value ? activeBg : colors.recessed;
    final effectiveFill = !widget.enabled
        ? fillColor.withValues(alpha: 0.45)
        : _pressed
        ? fillColor.withValues(alpha: 0.85)
        : fillColor;
    final borderColor = _focused
        ? colors.focus
        : _hovered
        ? colors.accent
        : widget.value
        ? activeBg
        : colors.hairline;
    final duration = widget.style.motion.duration;
    final curve = widget.style.motion.curve;
    final radius =
        widget.style.radius ?? (tokens.controlRadius / 2).clamp(4.0, 6.0);

    final checkbox = NakedCheckbox(
      value: widget.value,
      enabled: widget.enabled && widget.onChanged != null,
      semanticLabel: widget.label.isEmpty ? 'Checkbox' : widget.label,
      onChanged: widget.enabled && widget.onChanged != null
          ? (next) => widget.onChanged!(next ?? !widget.value)
          : null,
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
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Box(
            style: BoxStyler()
                .width(18)
                .height(18)
                .color(effectiveFill)
                .borderRadius(BorderRadiusGeometryMix.circular(radius))
                .border(
                  BoxBorderMix.all(
                    BorderSideMix.color(borderColor)
                        .width(_focused ? 1.5 : 1.0),
                  ),
                )
                .animate(
                  AnimationConfig.curve(duration: duration, curve: curve),
                ),
            child: Center(
              child: AnimatedScale(
                scale: widget.value ? 1.0 : 0.0,
                duration: duration,
                curve: curve,
                child: Icon(
                  uiIcon('check') ?? Icons.check,
                  size: 12,
                  color: Colors.white,
                ),
              ),
            ),
          ),
          if (widget.label.isNotEmpty) ...[
            SizedBox(width: widget.style.gap ?? tokens.spaceSmall),
            Flexible(
              child: Text(
                widget.label,
                style: TextStyle(
                  color: widget.enabled ? colors.strong : colors.muted,
                  fontSize: 13.5,
                  fontWeight: FontWeight.w500,
                ),
              ),
            ),
          ],
        ],
      ),
    );

    if (widget.semanticsId == null) return checkbox;
    return Semantics(
      identifier: widget.semanticsId,
      checked: widget.value,
      enabled: widget.enabled,
      child: checkbox,
    );
  }
}
