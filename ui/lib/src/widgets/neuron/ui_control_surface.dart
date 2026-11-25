import 'package:flutter/material.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_tokens.dart';

/// Motion curve & duration presets.
enum UiMotionPreset {
  instant(Duration.zero, Curves.linear),
  snappy(Duration(milliseconds: 110), Curves.easeOutCubic),
  standard(Duration(milliseconds: 180), Curves.easeOutCubic),
  expressive(Duration(milliseconds: 280), Curves.easeInOutCubicEmphasized),
  spring(Duration(milliseconds: 220), Curves.easeOutBack);

  const UiMotionPreset(this.duration, this.curve);
  final Duration duration;
  final Curve curve;
}

/// Snapshot of local 120fps pointer & focus interaction state.
@immutable
final class UiInteractionSnapshot {
  const UiInteractionSnapshot({
    this.isHovered = false,
    this.isPressed = false,
    this.isFocused = false,
    this.isEnabled = true,
  });

  final bool isHovered;
  final bool isPressed;
  final bool isFocused;
  final bool isEnabled;
}

/// Interaction & animation wrapper for Flutter UI controls: zero-latency local
/// hover, press-scale, and focus-ring transitions using [UiMotionPreset] &
/// [UiColors]. Hover, press and focus stay in the client; nothing is sent to
/// the server.
final class UiControlSurface extends StatefulWidget {
  const UiControlSurface({
    super.key,
    required this.builder,
    this.enabled = true,
    this.motion = UiMotionPreset.standard,
    this.pressScale = 0.985,
    this.borderRadius,
    this.onPointerEnter,
    this.onPointerLeave,
    this.onPointerDown,
    this.onPointerUp,
    this.onFocusChanged,
  });

  final Widget Function(BuildContext context, UiInteractionSnapshot state)
  builder;
  final bool enabled;
  final UiMotionPreset motion;
  final double pressScale;
  final BorderRadius? borderRadius;
  final VoidCallback? onPointerEnter;
  final VoidCallback? onPointerLeave;
  final VoidCallback? onPointerDown;
  final VoidCallback? onPointerUp;
  final ValueChanged<bool>? onFocusChanged;

  @override
  State<UiControlSurface> createState() => _UiControlSurfaceState();
}

class _UiControlSurfaceState extends State<UiControlSurface> {
  bool _hovered = false;
  bool _pressed = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final radius =
        widget.borderRadius ?? BorderRadius.circular(tokens.controlRadius);

    final snapshot = UiInteractionSnapshot(
      isHovered: widget.enabled && _hovered,
      isPressed: widget.enabled && _pressed,
      isFocused: widget.enabled && _focused,
      isEnabled: widget.enabled,
    );

    final duration = widget.motion.duration;
    final curve = widget.motion.curve;

    return Focus(
      canRequestFocus: widget.enabled,
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
        widget.onFocusChanged?.call(focused);
      },
      child: MouseRegion(
        cursor: widget.enabled
            ? SystemMouseCursors.click
            : SystemMouseCursors.basic,
        onEnter: widget.enabled
            ? (e) {
                if (_hovered) return;
                setState(() => _hovered = true);
                widget.onPointerEnter?.call();
              }
            : null,
        onExit: widget.enabled
            ? (e) {
                if (!_hovered && !_pressed) return;
                setState(() {
                  _hovered = false;
                  _pressed = false;
                });
                widget.onPointerLeave?.call();
              }
            : null,
        child: Listener(
          onPointerDown: widget.enabled
              ? (e) {
                  setState(() => _pressed = true);
                  widget.onPointerDown?.call();
                }
              : null,
          onPointerUp: widget.enabled
              ? (e) {
                  if (_pressed) setState(() => _pressed = false);
                  widget.onPointerUp?.call();
                }
              : null,
          onPointerCancel: widget.enabled
              ? (_) {
                  if (_pressed) setState(() => _pressed = false);
                }
              : null,
          child: AnimatedScale(
            scale: snapshot.isPressed ? widget.pressScale : 1.0,
            duration: duration,
            curve: curve,
            child: AnimatedContainer(
              duration: duration,
              curve: curve,
              decoration: BoxDecoration(
                borderRadius: radius,
                border: snapshot.isFocused
                    ? Border.all(color: colors.focus, width: 1.5)
                    : null,
              ),
              child: widget.builder(context, snapshot),
            ),
          ),
        ),
      ),
    );
  }
}
