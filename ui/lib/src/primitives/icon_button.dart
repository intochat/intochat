import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_tokens.dart';
import 'style.dart';

/// Option B `naked_ui` (`NakedButton`) + `mix` IconButton control with
/// hover/press/focus states and an optional tooltip.
final class UiMixIconButton extends StatefulWidget {
  const UiMixIconButton({
    super.key,
    required this.icon,
    required this.onPressed,
    this.iconSize = 16,
    this.iconColor,
    this.tooltip,
    this.size = 30,
    this.enabled = true,
    this.style = const UiStyleData(tone: 'subtle', paddingX: 6, paddingY: 6),
    this.semanticsId,
  });

  final IconData icon;
  final VoidCallback? onPressed;
  final double iconSize;
  final Color? iconColor;
  final String? tooltip;
  final double size;
  final bool enabled;
  final UiStyleData style;
  final String? semanticsId;

  @override
  State<UiMixIconButton> createState() => _UiMixIconButtonState();
}

class _UiMixIconButtonState extends State<UiMixIconButton> {
  bool _hovered = false;
  bool _pressed = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final isInteractive = widget.enabled && widget.onPressed != null;
    final radius = widget.style.radius ?? tokens.controlRadius;
    final bg = !isInteractive
        ? Colors.transparent
        : _pressed
        ? colors.recessed
        : _hovered
        ? colors.recessed.withValues(alpha: 0.7)
        : Colors.transparent;
    final borderColor = _focused
        ? colors.focus
        : _hovered && isInteractive
        ? colors.hairline
        : Colors.transparent;
    final fg =
        widget.iconColor ?? (isInteractive ? colors.strong : colors.muted);

    Widget button = NakedButton(
      enabled: isInteractive,
      onPressed: widget.onPressed,
      semanticLabel: widget.tooltip,
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
        scale: _pressed && isInteractive ? 0.94 : 1.0,
        duration: widget.style.motion.duration,
        curve: widget.style.motion.curve,
        child: Box(
          style: BoxStyler()
              .width(widget.size)
              .height(widget.size)
              .color(bg)
              .borderRadius(BorderRadiusGeometryMix.circular(radius))
              .border(
                BoxBorderMix.all(
                  BorderSideMix.color(borderColor).width(_focused ? 1.5 : 1.0),
                ),
              )
              .animate(
                AnimationConfig.curve(
                  duration: widget.style.motion.duration,
                  curve: widget.style.motion.curve,
                ),
              ),
          child: Center(
            child: Icon(
              widget.icon,
              size: widget.iconSize,
              color: isInteractive ? fg : fg.withValues(alpha: 0.45),
            ),
          ),
        ),
      ),
    );

    if (widget.tooltip != null && widget.tooltip!.isNotEmpty) {
      button = Tooltip(message: widget.tooltip!, child: button);
    }

    if (widget.semanticsId == null) return button;
    return Semantics(
      identifier: widget.semanticsId,
      button: true,
      enabled: isInteractive,
      child: button,
    );
  }
}
