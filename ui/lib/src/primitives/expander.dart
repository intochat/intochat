import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import 'ui_control_surface.dart';
import 'style.dart';

/// Option B `naked_ui` + `mix` Expander control with smooth spring chevron
/// rotation, animated body reveal, and exact C# [UiStyleData] padding/radius
/// parity.
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
    this.semanticsId,
  });

  final String header;
  final String subtitle;
  final bool isExpanded;
  final ValueChanged<bool> onExpandedChanged;
  final Widget body;
  final bool enabled;
  final UiStyleData style;
  final String? semanticsId;

  @override
  State<UiMixExpander> createState() => _UiMixExpanderState();
}

class _UiMixExpanderState extends State<UiMixExpander> {
  bool _hovered = false;
  bool _pressed = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final resolved = widget.style.resolve(
      hovered: _hovered && widget.enabled,
      pressed: _pressed && widget.enabled,
      focused: _focused && widget.enabled,
    );
    final duration = resolved.motion.duration;
    final curve = resolved.motion.curve;

    final card = Box(
      style: UiStyleMapper.boxStyler(
        context,
        widget.style,
        isHovered: _hovered,
        isPressed: _pressed,
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
                          style: TextStyle(color: colors.muted, fontSize: 12),
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
                    padding: EdgeInsets.only(top: resolved.gap ?? 10),
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
