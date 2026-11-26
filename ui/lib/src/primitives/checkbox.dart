import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_tokens.dart';
import 'ui_control_surface.dart';
import 'style.dart';

/// Option B `naked_ui` (`NakedCheckbox`) + `mix` Checkbox control with exact
/// C# [UiStyleData] parity and spring check reveal.
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
    this.semanticsId,
  });

  final bool value;
  final ValueChanged<bool>? onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final String? semanticsId;

  @override
  State<UiMixCheckbox> createState() => _UiMixCheckboxState();
}

class _UiMixCheckboxState extends State<UiMixCheckbox> {
  bool _hovered = false;
  bool _pressed = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final isInteractive = widget.enabled && widget.onChanged != null;
    final resolved = widget.style.resolve(
      hovered: _hovered && isInteractive,
      pressed: _pressed && isInteractive,
      focused: _focused && isInteractive,
    );
    final activeBg = UiStyleMapper.resolveToneBackground(colors, resolved.tone);
    final fillColor = widget.value ? activeBg : colors.recessed;
    final hasExplicitPressTone =
        _pressed && isInteractive && widget.style.press?.tone != null;
    final effectiveFill = !isInteractive
        ? fillColor.withValues(alpha: 0.45)
        : _pressed && !hasExplicitPressTone
        ? fillColor.withValues(alpha: 0.85)
        : fillColor;
    final borderColor = _focused
        ? colors.focus
        : _hovered && isInteractive
        ? colors.accent
        : widget.value
        ? activeBg
        : colors.hairline;
    final duration = resolved.motion.duration;
    final curve = resolved.motion.curve;
    final radius =
        resolved.radius ?? (tokens.controlRadius / 2).clamp(4.0, 6.0);

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
      },
      onPressChange: (pressed) {
        if (_pressed == pressed) return;
        setState(() => _pressed = pressed);
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
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
