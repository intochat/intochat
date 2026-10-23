import 'dart:async';

import 'package:flutter/material.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_shadows.dart';
import '../../theme/ui_theme.dart';
import 'ui_kit_surface.dart';

/// Tone of a kit toast. Neutral is the Figma toast; the others add a
/// leading status dot.
enum UiTone { neutral, success, error }

/// How long a kit toast stays up; matches ViewToast.
const kitToastLifetime = Duration(seconds: 4);

OverlayEntry? _current;
Timer? _timer;

void _hide() {
  _timer?.cancel();
  _timer = null;
  _current?.remove();
  _current = null;
}

/// Shows the Figma `.toast` (bottom 22, radius 10, padding 10x12, `strong`
/// fill, `page` text, [UiType.toast], shadow-lg) over the root overlay of
/// [context] (or [context] itself when it is an [Overlay]'s, e.g. a
/// navigator's `overlay.context`), replacing any toast already up. It hides after
/// [kitToastLifetime] or when its action runs.
void showKitToast(
  BuildContext context,
  String message, {
  UiTone tone = UiTone.neutral,
  String? actionLabel,
  VoidCallback? onAction,
}) {
  final overlay =
      Overlay.maybeOf(context, rootOverlay: true) ??
      switch (context) {
        StatefulElement(:final OverlayState state) => state,
        _ => null,
      };
  if (overlay == null) return;
  _hide();
  final entry = OverlayEntry(
    builder: (context) => _KitToast(
      message: message,
      tone: tone,
      actionLabel: actionLabel,
      onAction: onAction == null
          ? null
          : () {
              _hide();
              onAction();
            },
    ),
  );
  _current = entry;
  overlay.insert(entry);
  _timer = Timer(kitToastLifetime, () {
    if (identical(_current, entry)) _hide();
  });
}

final class _KitToast extends StatelessWidget {
  const _KitToast({
    required this.message,
    required this.tone,
    required this.actionLabel,
    required this.onAction,
  });
  final String message;
  final UiTone tone;
  final String? actionLabel;
  final VoidCallback? onAction;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final style = UiType.of(context).toast.copyWith(color: c.page);
    final dot = switch (tone) {
      UiTone.neutral => null,
      UiTone.success => c.done,
      UiTone.error => c.error,
    };
    final label = actionLabel;
    return Positioned(
      left: 0,
      right: 0,
      bottom: 22,
      child: Center(
        child: Material(
          type: MaterialType.transparency,
          child: Semantics(
            liveRegion: true,
            child: Container(
              key: const ValueKey('ui_kit_toast'),
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
              decoration: BoxDecoration(
                color: c.strong,
                borderRadius: BorderRadius.circular(10),
                boxShadow: UiShadows.lg(uiKitShadowBase(context)),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (dot != null) ...[
                    Container(
                      width: 8,
                      height: 8,
                      decoration: BoxDecoration(
                        color: dot,
                        shape: BoxShape.circle,
                      ),
                    ),
                    const SizedBox(width: 8),
                  ],
                  Flexible(child: Text(message, style: style)),
                  if (label != null) ...[
                    const SizedBox(width: 16),
                    TextButton(
                      onPressed: onAction,
                      style: ButtonStyle(
                        padding: const WidgetStatePropertyAll(EdgeInsets.zero),
                        minimumSize: const WidgetStatePropertyAll(Size.zero),
                        tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                        foregroundColor: WidgetStatePropertyAll(c.page),
                        overlayColor: const WidgetStatePropertyAll(
                          Colors.transparent,
                        ),
                        textStyle: WidgetStatePropertyAll(
                          style.copyWith(
                            fontWeight: FontWeight.w700,
                            decoration: TextDecoration.underline,
                            decorationColor: c.page,
                          ),
                        ),
                      ),
                      child: Text(label),
                    ),
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
