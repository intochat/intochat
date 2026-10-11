import 'dart:async';
import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flame/components.dart';
import 'package:flame/events.dart';
import 'package:flutter/foundation.dart';

import '../node_components.dart';

/// A signal travelling one edge, `t` from 0 (source) to 1 (target) over 600 ms.
final class BrainPulse {
  BrainPulse(this.from, this.to);
  final String from, to;
  double t = 0;
}

/// The brain as a slowly turning sphere: neurons on a Fibonacci sphere, synapses as edges and
/// every new activity as a pulse along its edge. Depth sets scale and alpha; a drag turns it.
final class BrainGraph extends NodeComponent {
  BrainGraph(super.node);

  static const pulseSeconds = 0.6;
  static const maxPulses = 256;
  static const idleSpin = 0.15;

  /// The `/brain/events` stream (`UiClient.watchBrain`) for `source=events`; the host that owns
  /// the client sets it, since a scene node has no client of its own.
  WatchBrain? get watch => _watch;
  set watch(WatchBrain? value) {
    _watch = value;
    _resubscribe();
  }

  WatchBrain? _watch;
  StreamSubscription<BrainSnapshot>? _subscription;
  String? _subscribedSource;

  BrainSnapshot? _snapshot;
  List<BrainNeuron> _nodes = const [];
  List<BrainSynapse> _edges = const [];
  Map<String, Vector3> _points = const {};
  final pulses = <BrainPulse>[];
  final _seen = <String>{};
  double _yaw = 0, _pitch = 0.3;

  bool animations = true;

  int get nodeCount => _nodes.length;
  int get edgeCount => _edges.length;
  String get source => params['source'] == 'static' ? 'static' : 'events';
  bool get appsOnly => params['filter'] == 'apps';
  String? get selected => params['selected'];
  String get layout => params['layout'] == 'force' ? 'force' : 'sphere';

  @override
  void changed() {
    size.setAll(param('size', 320).clamp(32, 8000).toDouble());
    super.changed();
    if (_snapshot != null) _project(_snapshot!);
    _resubscribe();
  }

  @override
  void onMount() {
    super.onMount();
    _resubscribe();
  }

  @override
  void onRemove() {
    _subscription?.cancel();
    _subscription = null;
    _subscribedSource = null;
    super.onRemove();
  }

  void _resubscribe() {
    final wanted = source == 'events' && _watch != null && isMounted;
    if (wanted && _subscribedSource == 'events' && _subscription != null) {
      return;
    }
    _subscription?.cancel();
    _subscription = null;
    _subscribedSource = null;
    if (!wanted) return;
    _subscribedSource = 'events';
    _subscription = _watch!().listen(show, onError: (Object _) {});
  }

  /// Draws [snapshot]; activity not seen before starts a pulse.
  void show(BrainSnapshot snapshot) {
    _snapshot = snapshot;
    _project(snapshot);
    for (final activity in snapshot.activity) {
      final key = '${activity.neuronId}:${activity.operationId ?? activity.id}';
      if (!_seen.add(key)) continue;
      if (_seen.length > 4096) _seen.remove(_seen.first);
      final edge = _edgeFor(activity);
      if (edge == null) continue;
      if (pulses.length >= maxPulses) pulses.removeAt(0);
      pulses.add(BrainPulse(edge.$1, edge.$2));
    }
    if (isMounted) game.wake();
  }

  (String, String)? _edgeFor(BrainActivity activity) {
    final id = activity.neuronId;
    if (!_points.containsKey(id)) return null;
    final target = activity.targetId;
    if (target != null && _points.containsKey(target)) return (id, target);
    for (final edge in _edges) {
      if (edge.targetId == id) return (edge.sourceId, id);
    }
    for (final edge in _edges) {
      if (edge.sourceId == id) return (id, edge.targetId);
    }
    return (id, id);
  }

  void _project(BrainSnapshot snapshot) {
    _nodes = snapshot.nodes
        .where((n) => !appsOnly || n.role == 'app' || n.role == 'synapse')
        .toList(growable: false);
    final ids = _nodes.map((n) => n.id).toSet();
    _edges = snapshot.synapses
        .where((s) => ids.contains(s.sourceId) && ids.contains(s.targetId))
        .toList(growable: false);
    // `force` uses the sphere layout for now: a spring pass on the sphere is a later refinement.
    _points = {
      for (final (i, n) in _nodes.indexed) n.id: _fibonacci(i, _nodes.length),
    };
    pulses.removeWhere(
      (p) => !_points.containsKey(p.from) || !_points.containsKey(p.to),
    );
  }

  static Vector3 _fibonacci(int i, int n) {
    if (n == 1) return Vector3(0, 0, 1);
    final y = 1 - 2 * i / (n - 1);
    final r = math.sqrt(math.max(0, 1 - y * y));
    final theta = math.pi * (3 - math.sqrt(5)) * i;
    return Vector3(math.cos(theta) * r, y, math.sin(theta) * r);
  }

  /// The rotated point: x, y on screen in [-1,1] and depth z in [-1,1] (1 nearest).
  Vector3 _rotated(Vector3 p) {
    final cy = math.cos(_yaw), sy = math.sin(_yaw);
    final x = p.x * cy + p.z * sy;
    final z1 = -p.x * sy + p.z * cy;
    final cp = math.cos(_pitch), sp = math.sin(_pitch);
    final y = p.y * cp - z1 * sp;
    final z = (p.y * sp + z1 * cp).clamp(-1.0, 1.0);
    return Vector3(x, y, z);
  }

  Vector2 _screen(Vector3 r) => size / 2 + Vector2(r.x, r.y) * (size.x * 0.4);

  @visibleForTesting
  Map<String, double> projectedDepths() => {
    for (final e in _points.entries) e.key: _rotated(e.value).z,
  };

  @override
  void update(double dt) {
    super.update(dt);
    var moving = false;
    if (animations && _nodes.isNotEmpty) {
      _yaw += idleSpin * dt;
      moving = true;
    }
    for (final pulse in pulses) {
      pulse.t += dt / pulseSeconds;
    }
    pulses.removeWhere((p) => p.t >= 1);
    if (moving || pulses.isNotEmpty) game.wake();
  }

  @override
  bool containsLocalPoint(Vector2 point) =>
      point.x >= 0 && point.y >= 0 && point.x <= size.x && point.y <= size.y;

  @override
  void onTapUp(TapUpEvent event) {
    game.wake();
    final hit = nodeAt(event.localPosition);
    if (hit != null && inputs.contains('tap')) {
      game.report(this, 'tap', event.localPosition, hit);
    } else {
      event.continuePropagation = true;
    }
  }

  /// The nearest projected neuron within its drawn radius of [local], front-most first.
  String? nodeAt(Vector2 local) {
    String? best;
    var bestZ = -2.0;
    for (final e in _points.entries) {
      final r = _rotated(e.value);
      final radius = 6 * _scale(r.z) + 4;
      if (_screen(r).distanceTo(local) <= radius && r.z > bestZ) {
        best = e.key;
        bestZ = r.z;
      }
    }
    return best;
  }

  // Drag turns the sphere; it is presentation, never reported.
  @override
  void onDragUpdate(DragUpdateEvent event) {
    game.wake();
    _yaw += event.localDelta.x * 0.01;
    _pitch = (_pitch + event.localDelta.y * 0.01).clamp(-1.4, 1.4);
  }

  @override
  void onDragEnd(DragEndEvent event) {
    super.onDragEnd(event);
    game.wake();
  }

  static double _scale(double z) => 0.6 + (z + 1) / 2 * 0.6;
  static double _alpha(double z) => 0.35 + (z + 1) / 2 * 0.65;

  @override
  void render(ui.Canvas canvas) {
    final projected = {
      for (final e in _points.entries) e.key: _rotated(e.value),
    };
    final line = ui.Paint()..strokeWidth = 1;
    for (final edge in _edges) {
      final a = projected[edge.sourceId]!, b = projected[edge.targetId]!;
      line.color = const ui.Color(0xFF8BAFA3)
          .withValues(alpha: _alpha((a.z + b.z) / 2) * 0.6);
      canvas.drawLine(_screen(a).toOffset(), _screen(b).toOffset(), line);
    }
    final order = projected.entries.toList()
      ..sort((a, b) => a.value.z.compareTo(b.value.z));
    final dot = ui.Paint();
    final chosen = selected;
    for (final e in order) {
      final z = e.value.z;
      dot.color =
          (e.key == chosen
                  ? const ui.Color(0xFF986A37)
                  : const ui.Color(0xFF397B63))
              .withValues(alpha: _alpha(z));
      canvas.drawCircle(_screen(e.value).toOffset(), 6 * _scale(z), dot);
    }
    final glow = ui.Paint()..color = const ui.Color(0xFFC2D9B7);
    for (final pulse in pulses) {
      final a = projected[pulse.from], b = projected[pulse.to];
      if (a == null || b == null) continue;
      final at = _screen(a) + (_screen(b) - _screen(a)) * pulse.t;
      canvas.drawCircle(at.toOffset(), 3, glow);
    }
  }
}
