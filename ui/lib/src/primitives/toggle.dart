import 'dart:async';

import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../ui/ui_control_surface.dart';
import 'style.dart';

/// Option B `naked_ui` (`NakedToggle(asSwitch: true)`) + `mix` toggle control
/// with exact C# [UiStyleData] parity, 120fps spring thumb motion, and opt-in
/// WinForms-style [UiEventMask] signal dispatch.
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
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
    this.semanticLabel,
  });

  final bool value;
  final ValueChanged<bool>? onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;
  final String? semanticLabel;

  @override
  State<UiMixToggle> createState() => _UiMixToggleState();
}

class _UiMixToggleState extends State<UiMixToggle> {
  bool _hovered = false;
  bool _focused = false;

  void _emit(int flag, String method, List<Object?> args) {
    if (!widget.enabled || (widget.eventMask & flag) == 0) return;
    final emitter = widget.onEmit;
    if (emitter != null) unawaited(emitter(method, args));
  }

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final activeBg = UiStyleMapper.resolveToneBackground(
      colors,
      widget.style.tone,
    );
    final trackColor = widget.value ? activeBg : colors.recessed;
    final duration = widget.style.motion.duration;
    final curve = widget.style.motion.curve;

    final isInteractive = widget.enabled && widget.onChanged != null;
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
        _emit(
          UiEventMask.hover,
          hovered ? 'OnPointerEnter' : 'OnPointerLeave',
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
                .width(42)
                .height(24)
                .padding(EdgeInsetsGeometryMix.all(3))
                .color(
                  widget.enabled
                      ? trackColor
                      : trackColor.withValues(alpha: 0.45),
                )
                .borderRadius(BorderRadiusGeometryMix.circular(999))
                .border(
                  BoxBorderMix.all(
                    BorderSideMix.color(
                      _focused
                          ? colors.focus
                          : _hovered
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
            SizedBox(width: widget.style.gap ?? 10),
            Text(
              widget.label,
              style: TextStyle(
                color: widget.enabled ? colors.strong : colors.muted,
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
