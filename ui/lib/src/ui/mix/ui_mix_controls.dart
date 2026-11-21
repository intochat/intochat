import 'dart:async';

import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_icon.dart';
import '../ui_control_surface.dart';
import 'ui_mix_style.dart';

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
    this.style = const UiStyleData(tone: 'accent', motion: UiMotionPreset.spring),
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
  });

  final bool value;
  final ValueChanged<bool> onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;

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

    final toggle = NakedToggle(
      value: widget.value,
      enabled: widget.enabled,
      asSwitch: true,
      semanticLabel: widget.label.isEmpty ? 'Toggle' : widget.label,
      onChanged: widget.onChanged,
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

/// Option B `naked_ui` + `mix` Expander control with smooth spring chevron
/// rotation, animated body reveal, exact C# [UiStyleData] padding/radius
/// parity, and WinForms-style [UiEventMask] signals.
final class UiMixExpander extends StatefulWidget {
  const UiMixExpander({
    super.key,
    required this.header,
    required this.isExpanded,
    required this.onExpandedChanged,
    required this.body,
    this.subtitle = '',
    this.enabled = true,
    this.style = const UiStyleData(
      tone: 'card',
      paddingX: 14,
      paddingY: 12,
      radius: 10,
      motion: UiMotionPreset.standard,
    ),
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
  });

  final String header;
  final String subtitle;
  final bool isExpanded;
  final ValueChanged<bool> onExpandedChanged;
  final Widget body;
  final bool enabled;
  final UiStyleData style;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;

  @override
  State<UiMixExpander> createState() => _UiMixExpanderState();
}

class _UiMixExpanderState extends State<UiMixExpander> {
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
    final duration = widget.style.motion.duration;
    final curve = widget.style.motion.curve;

    final card = Box(
      style: UiStyleMapper.boxStyler(
        context,
        widget.style,
        isHovered: _hovered,
        isFocused: _focused,
        isEnabled: widget.enabled,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          NakedButton(
            enabled: widget.enabled,
            onPressed: () => widget.onExpandedChanged(!widget.isExpanded),
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
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        widget.header,
                        style: TextStyle(
                          color: colors.strong,
                          fontSize: 14,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                      if (widget.subtitle.isNotEmpty)
                        Text(
                          widget.subtitle,
                          style: TextStyle(
                            color: colors.muted,
                            fontSize: 12,
                          ),
                        ),
                    ],
                  ),
                ),
                AnimatedRotation(
                  turns: widget.isExpanded ? 0.5 : 0.0,
                  duration: duration,
                  curve: curve,
                  child: Icon(
                    uiIcon('chevron-down') ?? Icons.expand_more,
                    size: 18,
                    color: colors.muted,
                  ),
                ),
              ],
            ),
          ),
          AnimatedSize(
            duration: duration,
            curve: curve,
            alignment: Alignment.topCenter,
            child: widget.isExpanded
                ? Padding(
                    padding: EdgeInsets.only(top: widget.style.gap ?? 10),
                    child: widget.body,
                  )
                : const SizedBox.shrink(),
          ),
        ],
      ),
    );

    if (widget.semanticsId == null) return card;
    return Semantics(
      identifier: widget.semanticsId,
      expanded: widget.isExpanded,
      child: card,
    );
  }
}
