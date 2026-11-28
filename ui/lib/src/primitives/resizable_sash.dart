import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_tokens.dart';

/// Default hit-target width for a vertical [UiResizableSash].
const double kResizableSashHitWidth = 8;

/// Default keyboard arrow nudge step (in logical pixels) for [UiResizableSash].
const double kResizableSashKeyboardStep = 16;

class _NudgeSashIntent extends Intent {
  const _NudgeSashIntent(this.delta);

  final double delta;
}

/// Reusable horizontal-drag and keyboard-adjustable vertical split sash.
///
/// Owns its hover, focus, and drag highlight state internally, supports
/// pointer horizontal dragging, double-tap reset, keyboard `ArrowLeft` /
/// `ArrowRight` nudging, `ActivateIntent` (Enter / Space) reset, reduced-motion
/// durations, and `Semantics(slider: true)` annotations.
final class UiResizableSash extends StatefulWidget {
  const UiResizableSash({
    super.key,
    required this.label,
    required this.onDelta,
    this.onReset,
    this.semanticsIdentifier,
    this.semanticsHint,
    this.semanticsValue,
    this.enabled = true,
    this.hitWidth = kResizableSashHitWidth,
    this.keyboardStep = kResizableSashKeyboardStep,
  });

  /// Accessible slider label (e.g. `'Resize sidebar'`, `'Resize inspector'`).
  final String label;

  /// Optional automation/semantics identifier (e.g. `'key:shell-sash-sidebar'`).
  final String? semanticsIdentifier;

  /// Optional accessibility hint.
  final String? semanticsHint;

  /// Optional current value description for assistive technologies.
  final String? semanticsValue;

  /// Whether pointer and keyboard resizing interactions are active.
  final bool enabled;

  /// Hit-target width in logical pixels. Defaults to [kResizableSashHitWidth].
  final double hitWidth;

  /// Logical pixel delta per `ArrowLeft` / `ArrowRight` press.
  final double keyboardStep;

  /// Invoked with horizontal drag deltas or keyboard nudge steps.
  final ValueChanged<double> onDelta;

  /// Invoked on double-tap or keyboard `ActivateIntent` to restore defaults.
  final VoidCallback? onReset;

  @override
  State<UiResizableSash> createState() => _UiResizableSashState();
}

class _UiResizableSashState extends State<UiResizableSash> {
  bool _hovered = false;
  bool _dragging = false;
  bool _focused = false;

  @override
  void didUpdateWidget(covariant UiResizableSash oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!widget.enabled && oldWidget.enabled) {
      _hovered = false;
      _dragging = false;
      _focused = false;
    }
  }

  void _handleDelta(double delta) {
    if (!widget.enabled) return;
    widget.onDelta(delta);
  }

  void _handleReset() {
    if (!widget.enabled) return;
    widget.onReset?.call();
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final duration = MediaQuery.disableAnimationsOf(context)
        ? Duration.zero
        : tokens.fast;
    final active = _hovered || _dragging || _focused;
    final interactive = widget.enabled;

    return Semantics(
      identifier: widget.semanticsIdentifier,
      label: widget.label,
      hint: widget.semanticsHint,
      value: widget.semanticsValue,
      slider: true,
      enabled: interactive,
      child: FocusableActionDetector(
        enabled: interactive,
        onShowFocusHighlight: (f) => setState(() => _focused = f),
        onShowHoverHighlight: (h) => setState(() => _hovered = h),
        mouseCursor: interactive
            ? SystemMouseCursors.resizeLeftRight
            : MouseCursor.defer,
        shortcuts: {
          const SingleActivator(LogicalKeyboardKey.arrowLeft): _NudgeSashIntent(
            -widget.keyboardStep,
          ),
          const SingleActivator(LogicalKeyboardKey.arrowRight):
              _NudgeSashIntent(widget.keyboardStep),
        },
        actions: {
          _NudgeSashIntent: CallbackAction<_NudgeSashIntent>(
            onInvoke: (intent) {
              _handleDelta(intent.delta);
              return null;
            },
          ),
          if (widget.onReset != null)
            ActivateIntent: CallbackAction<ActivateIntent>(
              onInvoke: (_) {
                _handleReset();
                return null;
              },
            ),
        },
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onDoubleTap: interactive && widget.onReset != null
              ? _handleReset
              : null,
          onHorizontalDragStart: interactive
              ? (_) => setState(() => _dragging = true)
              : null,
          onHorizontalDragUpdate: interactive
              ? (details) => _handleDelta(details.delta.dx)
              : null,
          onHorizontalDragEnd: interactive
              ? (_) => setState(() => _dragging = false)
              : null,
          onHorizontalDragCancel: interactive
              ? () => setState(() => _dragging = false)
              : null,
          child: SizedBox(
            width: widget.hitWidth,
            child: Center(
              child: AnimatedContainer(
                duration: duration,
                width: active ? 3 : 1,
                constraints: const BoxConstraints(minHeight: 24),
                decoration: BoxDecoration(
                  color: (_dragging || _focused)
                      ? c.accent
                      : (_hovered
                            ? c.accent.withValues(alpha: 0.65)
                            : c.borderHairline),
                  borderRadius: BorderRadius.circular(2),
                  boxShadow: _focused
                      ? [BoxShadow(color: c.ring, spreadRadius: 2)]
                      : null,
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
