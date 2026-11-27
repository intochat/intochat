import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/mix_tokens.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_tokens.dart';
import 'style.dart';

/// Option B `naked_ui` (`NakedButton.builder`) + `mix` IconButton control with
/// hover/press/focus states and an optional tooltip.
final class UiMixIconButton extends StatelessWidget {
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
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final isInteractive = enabled && onPressed != null;
    final fg = iconColor ?? (isInteractive ? colors.strong : colors.muted);

    Widget button = NakedButton(
      enabled: isInteractive,
      onPressed: onPressed,
      semanticLabel: tooltip,
      builder: (context, buttonState, _) {
        final hovered = buttonState.isHovered && isInteractive;
        final pressed = buttonState.isPressed && isInteractive;
        final focused = buttonState.isFocused && isInteractive;
        final resolved = style.resolve(
          hovered: hovered,
          pressed: pressed,
          focused: focused,
        );
        final radius = resolved.radius ?? tokens.controlRadius;
        final hasToneOverride =
            (pressed && style.press?.tone != null) ||
            (hovered && style.hover?.tone != null) ||
            (focused && style.focus?.tone != null);
        final bg = !isInteractive
            ? Colors.transparent
            : hasToneOverride
            ? UiStyleMapper.resolveToneBackgroundToken(resolved.tone)
            : pressed
            ? UiMixTokens.recessed()
            : hovered
            ? UiMixTokens.recessed().withValues(alpha: 0.7)
            : Colors.transparent;
        final borderColor = focused
            ? UiMixTokens.focus()
            : hovered
            ? UiMixTokens.hairline()
            : Colors.transparent;

        return AnimatedScale(
          scale: resolved.scale ?? (pressed ? 0.94 : 1.0),
          duration: resolved.motion.duration,
          curve: resolved.motion.curve,
          child: Box(
            style: BoxStyler()
                .width(size)
                .height(size)
                .color(bg)
                .borderRadius(BorderRadiusGeometryMix.circular(radius))
                .border(
                  BoxBorderMix.all(
                    BorderSideMix.color(borderColor).width(focused ? 1.5 : 1.0),
                  ),
                )
                .animate(
                  AnimationConfig.curve(
                    duration: resolved.motion.duration,
                    curve: resolved.motion.curve,
                  ),
                ),
            child: Center(
              child: Icon(
                icon,
                size: iconSize,
                color: isInteractive ? fg : fg.withValues(alpha: 0.45),
              ),
            ),
          ),
        );
      },
    );

    if (tooltip != null && tooltip!.isNotEmpty) {
      button = Tooltip(message: tooltip!, child: button);
    }

    if (semanticsId == null) return button;
    return Semantics(identifier: semanticsId, child: button);
  }
}
