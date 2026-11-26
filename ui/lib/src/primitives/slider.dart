import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import 'ui_control_surface.dart';
import 'style.dart';

/// Option B `naked_ui` (`NakedSlider`) + `mix` Slider control with smooth
/// track fill, draggable thumb, and [UiColors] styling.
final class UiMixSlider extends StatefulWidget {
  const UiMixSlider({
    super.key,
    required this.value,
    this.min = 0.0,
    this.max = 100.0,
    this.step = 1.0,
    required this.onChanged,
    this.onChangeEnd,
    this.enabled = true,
    this.style = const UiStyleData(
      tone: 'accent',
      motion: UiMotionPreset.standard,
    ),
    this.semanticsId,
  });

  final double value;
  final double min;
  final double max;
  final double step;
  final ValueChanged<double>? onChanged;
  final ValueChanged<double>? onChangeEnd;
  final bool enabled;
  final UiStyleData style;
  final String? semanticsId;

  @override
  State<UiMixSlider> createState() => _UiMixSliderState();
}

class _UiMixSliderState extends State<UiMixSlider> {
  bool _hovered = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final isInteractive = widget.enabled && widget.onChanged != null;
    final safeMax = widget.max > widget.min ? widget.max : widget.min + 1.0;
    final safeStep = widget.step > 0 && widget.step.isFinite
        ? widget.step
        : 1.0;
    final clamped = widget.value.clamp(widget.min, safeMax);
    final resolved = widget.style.resolve(
      hovered: _hovered && isInteractive,
      focused: _focused && isInteractive,
    );
    final activeColor = UiStyleMapper.resolveToneBackground(
      colors,
      resolved.tone,
    );

    final slider = NakedSlider(
      values: [clamped],
      min: widget.min,
      max: safeMax,
      step: safeStep,
      enabled: isInteractive,
      onChanged: isInteractive ? (vals) => widget.onChanged!(vals.first) : null,
      onChangeEnd: isInteractive && widget.onChangeEnd != null
          ? (vals) => widget.onChangeEnd!(vals.first)
          : null,
      onHoverChange: (hovered) {
        if (_hovered == hovered) return;
        setState(() => _hovered = hovered);
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
      },
      builder: (context, state, _) {
        final fraction = state.visualPercentageAt(0).clamp(0.0, 1.0);
        return SizedBox(
          height: 28,
          child: LayoutBuilder(
            builder: (context, constraints) {
              final width = constraints.maxWidth.isFinite
                  ? constraints.maxWidth
                  : 200.0;
              const thumbSize = 16.0;
              final trackWidth = (width - thumbSize).clamp(
                0.0,
                double.infinity,
              );
              final thumbLeft = trackWidth * fraction;

              return Stack(
                alignment: Alignment.centerLeft,
                children: [
                  Padding(
                    padding: const EdgeInsets.symmetric(
                      horizontal: thumbSize / 2,
                    ),
                    child: NakedSliderTrack(
                      child: Box(
                        style: BoxStyler()
                            .height(4)
                            .color(colors.recessed)
                            .borderRadius(
                              BorderRadiusGeometryMix.circular(999),
                            ),
                        child: Align(
                          alignment: Alignment.centerLeft,
                          child: FractionallySizedBox(
                            widthFactor: fraction,
                            child: Box(
                              style: BoxStyler()
                                  .height(4)
                                  .color(
                                    isInteractive
                                        ? activeColor
                                        : activeColor.withValues(alpha: 0.45),
                                  )
                                  .borderRadius(
                                    BorderRadiusGeometryMix.circular(999),
                                  ),
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
                  Positioned(
                    left: thumbLeft,
                    child: Box(
                      style: BoxStyler()
                          .width(thumbSize)
                          .height(thumbSize)
                          .color(Colors.white)
                          .borderRadius(BorderRadiusGeometryMix.circular(999))
                          .border(
                            BoxBorderMix.all(
                              BorderSideMix.color(
                                _focused
                                    ? colors.focus
                                    : _hovered || state.isDragging
                                    ? activeColor
                                    : colors.hairline,
                              ).width(_focused || state.isDragging ? 2.0 : 1.5),
                            ),
                          ),
                    ),
                  ),
                ],
              );
            },
          ),
        );
      },
    );

    if (widget.semanticsId == null) return slider;
    return Semantics(
      identifier: widget.semanticsId,
      enabled: isInteractive,
      child: slider,
    );
  }
}
