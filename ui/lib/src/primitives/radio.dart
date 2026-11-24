import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_tokens.dart';
import '../widgets/neuron/ui_control_surface.dart';
import 'style.dart';

/// Option B `naked_ui` (`NakedRadio<T>`) + `mix` Radio control with spring dot
/// reveal and [UiColors]/[UiTokens] styling.
final class UiMixRadio<T> extends StatefulWidget {
  const UiMixRadio({
    super.key,
    required this.value,
    required this.groupValue,
    required this.onChanged,
    this.label = '',
    this.enabled = true,
    this.style = const UiStyleData(
      tone: 'accent',
      motion: UiMotionPreset.spring,
    ),
    this.semanticsId,
  });

  final T value;
  final T? groupValue;
  final ValueChanged<T>? onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final String? semanticsId;

  @override
  State<UiMixRadio<T>> createState() => _UiMixRadioState<T>();
}

class _UiMixRadioState<T> extends State<UiMixRadio<T>> {
  bool _hovered = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final isSelected = widget.value == widget.groupValue;
    final isInteractive = widget.enabled && widget.onChanged != null;
    final activeColor = UiStyleMapper.resolveToneBackground(
      colors,
      widget.style.tone,
    );
    final borderColor = _focused
        ? colors.focus
        : _hovered && isInteractive
        ? colors.accent
        : isSelected
        ? activeColor
        : colors.hairline;
    final duration = widget.style.motion.duration;
    final curve = widget.style.motion.curve;

    final radio = RadioGroup<T>(
      groupValue: widget.groupValue,
      onChanged: (next) {
        if (isInteractive && next != null) widget.onChanged!(next);
      },
      child: NakedRadio<T>(
        value: widget.value,
        enabled: isInteractive,
        semanticLabel: widget.label.isEmpty ? '${widget.value}' : widget.label,
        onHoverChange: (hovered) {
          if (_hovered == hovered) return;
          setState(() => _hovered = hovered);
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
                  .color(colors.recessed)
                  .borderRadius(BorderRadiusGeometryMix.circular(999))
                  .border(
                    BoxBorderMix.all(
                      BorderSideMix.color(borderColor)
                          .width(_focused || isSelected ? 1.75 : 1.0),
                    ),
                  )
                  .animate(
                    AnimationConfig.curve(duration: duration, curve: curve),
                  ),
              child: Center(
                child: AnimatedScale(
                  scale: isSelected ? 1.0 : 0.0,
                  duration: duration,
                  curve: curve,
                  child: Container(
                    width: 8,
                    height: 8,
                    decoration: BoxDecoration(
                      color: isInteractive
                          ? activeColor
                          : activeColor.withValues(alpha: 0.45),
                      shape: BoxShape.circle,
                    ),
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
                    color: isInteractive ? colors.body : colors.muted,
                    fontSize: 13.5,
                  ),
                ),
              ),
            ],
          ],
        ),
      ),
    );

    if (widget.semanticsId == null) return radio;
    return Semantics(
      identifier: widget.semanticsId,
      selected: isSelected,
      enabled: isInteractive,
      child: radio,
    );
  }
}
