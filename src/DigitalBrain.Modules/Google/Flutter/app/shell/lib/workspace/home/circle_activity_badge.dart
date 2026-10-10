import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter/material.dart';

typedef Pause = Future<void> Function(Duration delay);

// Counts the circle touches that need a look (someone spoke, someone invited you). It owns the live
// subscription, so it lives above Home: it keeps counting while Home is hidden. It reconnects only when the
// stream fails or closes, with growing pauses. Touches are told apart by retention sequence: the first
// replay of a cold start only seeds the high-water mark; afterwards only a sequence above it is news.
class CircleActivityFeed extends ChangeNotifier {
  CircleActivityFeed(
    this._watch, {
    Pause? pause,
    this.firstPause = const Duration(seconds: 1),
    this.longestPause = const Duration(seconds: 30),
  }) : _pause = pause ?? Future.delayed {
    _connect();
  }

  final Stream<CircleTouched> Function() _watch;
  final Pause _pause;
  final Duration firstPause, longestPause;
  int _count = 0;
  int get count => _count;
  StreamSubscription<CircleTouched>? _subscription;
  int? _highest;
  bool _disposed = false;
  Duration _next = Duration.zero;

  void _connect() {
    if (_disposed) return;
    try {
      _subscription = _watch().listen(
        _touched,
        onError: (Object _) => _reconnect(),
        onDone: _reconnect,
        cancelOnError: true,
      );
    } catch (_) {
      _reconnect();
    }
  }

  void _reconnect() {
    if (_disposed) return;
    _subscription?.cancel();
    _next = _next == Duration.zero
        ? firstPause
        : Duration(
            milliseconds: (_next.inMilliseconds * 2).clamp(
              0,
              longestPause.inMilliseconds,
            ),
          );
    _pause(_next).then((_) => _connect());
  }

  void _touched(CircleTouched touch) {
    _next = Duration.zero;
    final sequence = touch.sequence;
    var news = true;
    if (sequence != null) {
      final seen = _highest;
      news = seen == null ? !touch.replay : sequence > seen;
      if (seen == null || sequence > seen) _highest = sequence;
    }
    if (!news || (touch.kind != 'spoke' && touch.kind != 'invited')) return;
    _count++;
    notifyListeners();
  }

  void clear() {
    if (_count == 0) return;
    _count = 0;
    notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _subscription?.cancel();
    super.dispose();
  }
}

// Pins the feed's count to the circle app's icon. The count runs from the last time Home was shown:
// it is shown while Home is, and cleared when Home goes away.
class CircleActivityBadge extends StatefulWidget {
  const CircleActivityBadge({
    super.key,
    required this.feed,
    required this.child,
    this.homeShown = true,
  });

  final CircleActivityFeed feed;
  final Widget child;
  final bool homeShown;

  @override
  State<CircleActivityBadge> createState() => _CircleActivityBadgeState();
}

class _CircleActivityBadgeState extends State<CircleActivityBadge> {
  void _changed() => setState(() {});

  @override
  void initState() {
    super.initState();
    widget.feed.addListener(_changed);
  }

  @override
  void didUpdateWidget(CircleActivityBadge old) {
    super.didUpdateWidget(old);
    if (old.feed != widget.feed) {
      old.feed.removeListener(_changed);
      widget.feed.addListener(_changed);
    }
    if (old.homeShown && !widget.homeShown) widget.feed.clear();
  }

  @override
  void dispose() {
    widget.feed.removeListener(_changed);
    widget.feed.clear();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final count = widget.feed.count;
    final reduced = MediaQuery.disableAnimationsOf(context);
    final scheme = Theme.of(context).colorScheme;
    return Stack(
      clipBehavior: Clip.none,
      children: [
        widget.child,
        Positioned(
          top: -4,
          right: -4,
          child: ExcludeSemantics(
            excluding: count == 0,
            child: AnimatedScale(
              scale: count == 0 ? 0 : 1,
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
                  label: '$count new circle updates',
                  child: Text(
                    count > 99 ? '99+' : '$count',
                    style: TextStyle(color: scheme.onError, fontSize: 11),
                  ),
                ),
              ),
            ),
          ),
        ),
      ],
    );
  }
}
