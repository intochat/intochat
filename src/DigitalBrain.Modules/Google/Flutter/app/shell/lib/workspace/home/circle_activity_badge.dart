import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter/material.dart';

// Counts the circle touches that need a look (someone spoke, someone invited you) and pins the count to
// the circle app's icon. The count runs from the last time Home was shown: it keeps growing while Home is
// hidden, stays visible while Home is shown, and is cleared when Home is hidden again.
class CircleActivityBadge extends StatefulWidget {
  const CircleActivityBadge({
    super.key,
    required this.watch,
    required this.child,
    this.homeShown = true,
  });

  final Stream<CircleTouched> Function() watch;
  final Widget child;
  final bool homeShown;

  @override
  State<CircleActivityBadge> createState() => _CircleActivityBadgeState();
}

class _CircleActivityBadgeState extends State<CircleActivityBadge> {
  int _count = 0;
  StreamSubscription<CircleTouched>? _subscription;

  @override
  void initState() {
    super.initState();
    _subscription = widget.watch().listen((touch) {
      if (touch.kind == 'spoke' || touch.kind == 'invited') {
        setState(() => _count++);
      }
    }, onError: (Object _) {});
  }

  @override
  void didUpdateWidget(CircleActivityBadge old) {
    super.didUpdateWidget(old);
    if (old.homeShown && !widget.homeShown) _count = 0;
  }

  @override
  void dispose() {
    _subscription?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final reduced = MediaQuery.disableAnimationsOf(context);
    final scheme = Theme.of(context).colorScheme;
    return Stack(
      clipBehavior: Clip.none,
      children: [
        widget.child,
        Positioned(
          top: -4,
          right: -4,
          child: AnimatedScale(
            scale: _count == 0 ? 0 : 1,
            duration: reduced
                ? Duration.zero
                : const Duration(milliseconds: 160),
            child: Container(
              key: const ValueKey('circle-badge'),
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
              decoration: BoxDecoration(
                color: scheme.error,
                borderRadius: BorderRadius.circular(10),
              ),
              child: Semantics(
                label: '$_count new circle updates',
                child: Text(
                  _count > 99 ? '99+' : '$_count',
                  style: TextStyle(color: scheme.onError, fontSize: 11),
                ),
              ),
            ),
          ),
        ),
      ],
    );
  }
}
