import 'dart:async';
import 'dart:math' as math;

import 'package:flame/game.dart';
import 'package:flutter/material.dart' hide Focus;
import 'package:flutter/scheduler.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import '../src/ui/ino/ino_orb_v2.dart';

/// How long the sphere flies to a focus; a cut under reduced motion.
const presenceFlight = Duration(milliseconds: 420);

/// Ino's presence over the desktop: a glass sphere whose mood follows [presence] and, when the presence points
/// at something, a ring the layer draws around that target. Only the sphere and the ring take pointers.
class PresenceLayer extends StatefulWidget {
  const PresenceLayer({
    super.key,
    required this.presence,
    required this.enabled,
    required this.onAction,
    required this.onOpenIno,
    this.registry,
  });

  final Stream<Presence> presence;
  final bool enabled;
  final void Function(FocusAction) onAction;
  final VoidCallback onOpenIno;

  /// Where focus targets resolve; [FocusRegistry.instance] when null.
  final FocusRegistry? registry;

  @override
  State<PresenceLayer> createState() => PresenceLayerState();
}

class PresenceLayerState extends State<PresenceLayer>
    with SingleTickerProviderStateMixin {
  static const double sphereRadius = 28;
  static const double _phaseShift = math.pi / 2;

  StreamSubscription<Presence>? _sub;
  Presence? _presence;
  bool _dropped = false;
  late final Ticker _ticker = createTicker(_tick);
  Duration _lastTick = Duration.zero;
  late final _PresenceGame _game = _PresenceGame(this);

  final ValueNotifier<Offset?> _center = ValueNotifier(null);
  Size _size = Size.zero;
  double _tau = 0;
  double _clock = 0;
  Offset? _anchor;

  Offset? _flightFrom, _flightTo;
  double _flightElapsed = 0;

  Rect? _ring;
  FocusTone _tone = FocusTone.info;
  String? _toast;
  Timer? _toastTimer;

  FocusRegistry get _registry => widget.registry ?? FocusRegistry.instance;

  /// What the sphere shows: disconnected once the watch errors or ends.
  PresenceState get state => _dropped
      ? PresenceState.disconnected
      : (_presence?.state ?? PresenceState.idle);
  String? get neuronKind => _presence?.neuronKind;

  /// The sphere's centre in the layer's coordinates.
  Offset? get sphereCenter => _center.value;

  /// The ring's rect in the layer's coordinates, or null when nothing is focused.
  Rect? get ringRect => _ring;
  bool get reducedMotion =>
      MediaQuery.maybeDisableAnimationsOf(context) ?? false;

  @override
  void initState() {
    super.initState();
    _listen();
    _ticker.start();
  }

  @override
  void didUpdateWidget(PresenceLayer oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.presence != widget.presence) _listen();
  }

  void _listen() {
    _sub?.cancel();
    _dropped = false;
    _sub = widget.presence.listen(
      _apply,
      onError: (Object _) => _drop(),
      onDone: _drop,
    );
  }

  void _drop() {
    if (!mounted) return;
    setState(() => _dropped = true);
  }

  void _apply(Presence next) {
    if (!mounted) return;
    final focusChanged =
        _presence?.focus?.target != next.focus?.target ||
        _presence?.focus?.tone != next.focus?.tone;
    setState(() {
      _presence = next;
      _dropped = false;
    });
    if (focusChanged || next.focus == null) {
      // The target's rect exists only after layout.
      SchedulerBinding.instance.addPostFrameCallback((_) => _focus(next.focus));
    }
  }

  void _focus(Focus? focus) {
    if (!mounted) return;
    if (focus == null) {
      setState(() {
        _ring = null;
        _flightFrom = _flightTo = null;
      });
      return;
    }
    final rect = _resolve(focus.target);
    if (rect == null) {
      setState(() => _ring = null);
      _showToast(focus.reason);
      return;
    }
    final from = _center.value ?? rect.center;
    final corner = _nearestCorner(rect, from);
    setState(() {
      _ring = rect;
      _tone = focus.tone;
    });
    if (reducedMotion) {
      _flightFrom = _flightTo = null;
      _center.value = corner;
    } else {
      _flightFrom = from;
      _flightTo = corner;
      _flightElapsed = 0;
    }
  }

  /// The target's rect in layer coordinates: the target itself, else its app's window when the target's name
  /// carries an app (`.../apps/{appId}/...`).
  Rect? _resolve(String target) {
    final direct = _local(_registry.resolve(target));
    if (direct != null || target.startsWith('window:')) return direct;
    final app = RegExp(r'apps/([^/]+)').firstMatch(target)?.group(1);
    return app == null ? null : _local(_registry.resolve('window:$app'));
  }

  Rect? _local(Rect? global) {
    if (global == null) return null;
    final box = context.findRenderObject();
    if (box is! RenderBox || !box.hasSize) return global;
    return global.shift(box.globalToLocal(Offset.zero));
  }

  Offset _nearestCorner(Rect rect, Offset from) {
    final corners = [
      rect.topLeft,
      rect.topRight,
      rect.bottomLeft,
      rect.bottomRight,
    ];
    corners.sort(
      (a, b) =>
          (a - from).distanceSquared.compareTo((b - from).distanceSquared),
    );
    return corners.first;
  }

  void _showToast(String reason) {
    _toastTimer?.cancel();
    setState(() => _toast = reason);
    _toastTimer = Timer(const Duration(seconds: 4), () {
      if (mounted) setState(() => _toast = null);
    });
  }

  Offset _lissajous(double tau) {
    final c = _size.center(Offset.zero);
    return c +
        Offset(
          _size.width * 0.35 * math.sin(3 * tau + _phaseShift),
          _size.height * 0.3 * math.sin(2 * tau),
        );
  }

  List<Rect> _windows() => [
    for (final target in _registry.targets.toList())
      if (target.startsWith('window:'))
        if (_local(_registry.resolve(target)) case final rect?)
          rect.inflate(sphereRadius),
  ];

  /// The next idle point on the curve; points inside a window are skipped.
  Offset _drift(double tau) {
    final windows = _windows();
    var point = _lissajous(tau);
    for (var i = 0; i < 120 && windows.any((w) => w.contains(point)); i++) {
      _tau += 0.05;
      point = _lissajous(_tau);
    }
    return point;
  }

  void _tick(Duration elapsed) {
    final dt = (elapsed - _lastTick).inMicroseconds / 1e6;
    _lastTick = elapsed;
    if (!mounted || _size.isEmpty) return;
    _clock += dt;
    if (_center.value == null) {
      _center.value = _drift(_tau);
      return;
    }
    if (_flightFrom != null && _flightTo != null) {
      _flightElapsed += dt;
      final t = (_flightElapsed / (presenceFlight.inMicroseconds / 1e6)).clamp(
        0.0,
        1.0,
      );
      _center.value = Offset.lerp(
        _flightFrom,
        _flightTo,
        Curves.easeOutCubic.transform(t),
      );
      if (t >= 1) _flightFrom = _flightTo = null;
      return;
    }
    if (_ring != null || reducedMotion) return;
    switch (state) {
      case PresenceState.idle:
        _anchor = null;
        _tau += dt * 0.15;
        _center.value = _drift(_tau);
      case PresenceState.searching:
        _anchor ??= _center.value;
        _center.value = _anchor! + Offset(math.sin(_clock * 0.8) * 14, 0);
      default:
        _anchor = null;
    }
  }

  @override
  void dispose() {
    _sub?.cancel();
    _toastTimer?.cancel();
    _ticker.dispose();
    _center.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (!widget.enabled) return const SizedBox.shrink();
    final action = _presence?.focus?.action;
    return LayoutBuilder(
      builder: (context, constraints) {
        _size = constraints.biggest;
        final ring = _ring;
        return Stack(
          clipBehavior: Clip.none,
          children: [
            Positioned.fill(
              child: IgnorePointer(
                child: GameWidget<_PresenceGame>(game: _game, autofocus: false),
              ),
            ),
            if (ring != null && action != null)
              for (final (i, edge) in _edges(ring).indexed)
                Positioned.fromRect(
                  rect: edge,
                  child: GestureDetector(
                    key: ValueKey('presence-ring-$i'),
                    behavior: HitTestBehavior.opaque,
                    onTap: () => widget.onAction(action),
                  ),
                ),
            ValueListenableBuilder<Offset?>(
              valueListenable: _center,
              builder: (context, center, _) => center == null
                  ? const SizedBox.shrink()
                  : Positioned(
                      left: center.dx - sphereRadius,
                      top: center.dy - sphereRadius,
                      width: sphereRadius * 2,
                      height: sphereRadius * 2,
                      child: Semantics(
                        button: true,
                        label: 'Ino, ${state.name}',
                        child: GestureDetector(
                          key: const ValueKey('presence-sphere'),
                          behavior: HitTestBehavior.opaque,
                          onTap: widget.onOpenIno,
                          child: InoOrbV2(
                            state: state,
                            size: sphereRadius * 2,
                          ),
                        ),
                      ),
                    ),
            ),
            if (_toast case final toast?)
              Positioned(
                left: 0,
                right: 0,
                bottom: 72,
                child: IgnorePointer(
                  child: Center(
                    child: DecoratedBox(
                      key: const ValueKey('presence-toast'),
                      decoration: BoxDecoration(
                        color: const Color(0xE6202632),
                        borderRadius: BorderRadius.circular(10),
                      ),
                      child: Padding(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 14,
                          vertical: 8,
                        ),
                        child: Text(
                          toast,
                          style: const TextStyle(
                            color: Color(0xFFFFFFFF),
                            fontSize: 13,
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
          ],
        );
      },
    );
  }

  /// The ring's four border bands: the ring takes taps, the target inside it keeps its own.
  static List<Rect> _edges(Rect r) {
    const band = 10.0;
    return [
      Rect.fromLTRB(r.left - band, r.top - band, r.right + band, r.top + band),
      Rect.fromLTRB(
        r.left - band,
        r.bottom - band,
        r.right + band,
        r.bottom + band,
      ),
      Rect.fromLTRB(
        r.left - band,
        r.top + band,
        r.left + band,
        r.bottom - band,
      ),
      Rect.fromLTRB(
        r.right - band,
        r.top + band,
        r.right + band,
        r.bottom - band,
      ),
    ];
  }
}

final class _PresenceGame extends FlameGame {
  _PresenceGame(this._layer);

  final PresenceLayerState _layer;
  final sphere = GlassSphere(radius: PresenceLayerState.sphereRadius);
  final ring = HighlightRing();

  @override
  Color backgroundColor() => const Color(0x00000000);

  @override
  Future<void> onLoad() async {
    await add(ring);
    await add(sphere);
  }

  @override
  void update(double dt) {
    if (_layer.mounted) {
      final center = _layer.sphereCenter;
      sphere
        ..state = _layer.state
        ..neuronKind = _layer.neuronKind
        ..animations = !_layer.reducedMotion;
      if (center != null) sphere.position.setValues(center.dx, center.dy);
      final rect = _layer.ringRect;
      ring
        ..visible = rect != null
        ..tone = _layer._tone;
      if (rect != null) {
        ring
          ..position.setValues(rect.left, rect.top)
          ..size.setValues(rect.width, rect.height);
      }
    }
    super.update(dt);
  }
}
