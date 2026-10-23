import 'dart:async';

import 'package:flutter/material.dart';

import '../../components/kit/ui_kit_surface.dart';
import '../../theme/ui_colors.dart';
import '../../theme/ui_shadows.dart';
import '../../theme/ui_theme.dart';

/// Toast value node: bottom-centre overlay that hides itself after 4 s,
/// taking its single action button with it.
final class ViewToast extends StatefulWidget {
  const ViewToast({
    super.key,
    required this.node,
    required this.generation,
    required this.action,
  });
  final Map node;
  final int generation;
  final VoidCallback? Function(Map node) action;

  static const lifetime = Duration(seconds: 4);

  @override
  State<ViewToast> createState() => _ViewToastState();
}

class _ViewToastState extends State<ViewToast> {
  final _portal = OverlayPortalController();
  Timer? _timer;
  // The portal stays shown; visibility only swaps its content, because
  // OverlayPortalController.show may not run during build (didUpdateWidget).
  bool _visible = false;

  @override
  void initState() {
    super.initState();
    _portal.show();
    _show();
  }

  void _show() {
    _visible = true;
    _timer?.cancel();
    _timer = Timer(ViewToast.lifetime, () {
      if (mounted) setState(() => _visible = false);
    });
  }

  static Object? _action(Map node) => (node['children'] as List? ?? const [])
      .whereType<Map>()
      .where((m) => m['kind'] == 1)
      .map((m) => m['action'])
      .firstOrNull;

  // A republished toast (same key; new generation, text or action) shows
  // again.
  @override
  void didUpdateWidget(ViewToast old) {
    super.didUpdateWidget(old);
    if (old.generation != widget.generation ||
        old.node['text'] != widget.node['text'] ||
        _action(old.node) != _action(widget.node)) {
      _show();
    }
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => OverlayPortal(
    controller: _portal,
    overlayChildBuilder: _overlay,
    child: const SizedBox.shrink(),
  );

  Widget _overlay(BuildContext context) {
    if (!_visible) return const SizedBox.shrink();
    final c = UiColors.of(context);
    final style = UiType.of(context).toast.copyWith(color: c.page);
    final button = (widget.node['children'] as List? ?? const [])
        .whereType<Map>()
        .where((m) => m['kind'] == 1)
        .firstOrNull;
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
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
              decoration: BoxDecoration(
                color: c.strong,
                borderRadius: BorderRadius.circular(10),
                boxShadow: UiShadows.lg(uiKitShadowBase(context)),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Flexible(
                    child: Text(
                      widget.node['text'] as String? ?? '',
                      style: style,
                    ),
                  ),
                  if (button != null) ...[
                    const SizedBox(width: 16),
                    TextButton(
                      onPressed: widget.action(button),
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
                      child: Text(button['text'] as String? ?? ''),
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
