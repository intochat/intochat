import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import 'ui_control_surface.dart';
import 'style.dart';

/// Option B `naked_ui` (`NakedToggle(asSwitch: true)`) + `mix` toggle control
/// with exact C# [UiStyleData] parity and 120fps spring thumb motion.
final class UiMixToggle extends StatefulWidget {
  const UiMixToggle({
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
    this.semanticLabel,
  });

  final bool value;
  final ValueChanged<bool>? onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final String? semanticsId;
  final String? semanticLabel;

  @override
  State<UiMixToggle> createState() => _UiMixToggleState();
}

class _UiMixToggleState extends State<UiMixToggle> {
  bool _hovered = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final isInteractive = widget.enabled && widget.onChanged != null;
    final resolved = widget.style.resolve(
      hovered: _hovered && isInteractive,
      focused: _focused && isInteractive,
    );
    final activeBg = UiStyleMapper.resolveToneBackground(colors, resolved.tone);
    final trackColor = widget.value ? activeBg : colors.recessed;
    final duration = resolved.motion.duration;
    final curve = resolved.motion.curve;

    final toggle = NakedToggle(
      value: widget.value,
      enabled: isInteractive,
      asSwitch: true,
      semanticLabel:
          widget.semanticLabel ??
          (widget.label.isEmpty ? 'Toggle' : widget.label),
      onChanged: isInteractive ? widget.onChanged : null,
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
            key: const ValueKey('ui_toggle_track'),
            style: BoxStyler()
                .width(40)
                .height(22)
                .padding(EdgeInsetsGeometryMix.all(2))
                .color(
                  isInteractive
                      ? trackColor
                      : trackColor.withValues(alpha: 0.45),
                )
                .borderRadius(
                  BorderRadiusGeometryMix.circular(resolved.radius ?? 999),
                )
                .border(
                  BoxBorderMix.all(
                    BorderSideMix.color(
                      _focused
                          ? colors.focus
                          : _hovered && isInteractive
                          ? colors.accent
                          : colors.hairline,
                    ).width(_focused ? 1.5 : 1.0),
                  ),
                )
                .animate(
                  AnimationConfig.curve(duration: duration, curve: curve),
                ),
            child: AnimatedAlign(
              duration: duration,
              curve: curve,
              alignment: widget.value
                  ? Alignment.centerRight
                  : Alignment.centerLeft,
              child: Container(
                width: 16,
                height: 16,
                decoration: const BoxDecoration(
                  color: Colors.white,
                  shape: BoxShape.circle,
                ),
              ),
            ),
          ),
          if (widget.label.isNotEmpty) ...[
            SizedBox(width: resolved.gap ?? 10),
            Text(
              widget.label,
              style: TextStyle(
                color: isInteractive ? colors.strong : colors.muted,
                fontSize: 13.5,
                fontWeight: FontWeight.w500,
              ),
            ),
          ],
        ],
      ),
    );

    if (widget.semanticsId == null) return toggle;
    return Semantics(
      identifier: widget.semanticsId,
      toggled: widget.value,
      child: toggle,
    );
  }
}
