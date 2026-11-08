import 'dart:ui' as ui;

import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:flame/components.dart';
import 'package:flame/effects.dart';
import 'package:flame/events.dart';
import 'package:flutter/painting.dart' show HSVColor;

/// One drawn edge a pulse can travel: [from] is null for a `*` source (any publisher of [alias]).
final class PulseEdge {
  const PulseEdge({
    required this.key,
    required this.fromId,
    required this.toId,
    required this.alias,
    required this.start,
    required this.end,
  });
  final String key, fromId, toId, alias;
  final Vector2 start, end;
  Vector2 get midpoint => (start + end)..scale(.5);
}

/// One pulse in flight: it starts at [start], reaches [end] after [SignalPulses.travel].
final class SignalPulse {
  SignalPulse({
    required this.edge,
    required this.traceId,
    required this.start,
    required this.end,
  });
  final PulseEdge edge;
  final String traceId;
  final Vector2 start, end;
  double elapsed = 0;
  String get key => 'pulse:$traceId';
  ui.Color get color => pulseColor(traceId);
}

/// The colour of a trace: its id hashed onto the hue wheel, so one trace keeps one colour everywhere.
ui.Color pulseColor(String traceId) {
  var hash = 0x811C9DC5;
  for (final unit in traceId.codeUnits) {
    hash = ((hash ^ unit) * 0x01000193) & 0xFFFFFFFF;
  }
  return HSVColor.fromAHSV(1, (hash % 360).toDouble(), .7, 1).toColor();
}

/// The journal tail turned into pulses: each signal travels every edge whose alias it carries into a drawn node. Bounded:
/// at most [cap] pulses fly at once, and a signal whose trace already crosses the same edge joins it instead of spawning.
final class SignalPulses {
  SignalPulses({this.cap = 64});

  static const travel = Duration(milliseconds: 600);
  final int cap;
  final _edges = <PulseEdge>[];
  final _positions = <String, Vector2>{};
  final pulses = <SignalPulse>[];

  /// Replaces the drawn edges and node positions (the scene changed).
  void layout(Iterable<PulseEdge> edges, Map<String, Vector2> positions) {
    _edges
      ..clear()
      ..addAll(edges);
    _positions
      ..clear()
      ..addAll(positions);
  }

  /// Spawns the pulses of [signals]; returns those it added.
  List<SignalPulse> feed(Iterable<JournalTailSignal> signals) {
    final added = <SignalPulse>[];
    for (final signal in signals) {
      final trace = signal.traceId ?? 'seq:${signal.seq}';
      for (final edge in _edges) {
        if (edge.alias != signal.alias || !_positions.containsKey(edge.toId)) {
          continue;
        }
        if (pulses.length >= cap) return added;
        if (pulses.any((p) => p.edge.key == edge.key && p.traceId == trace)) {
          continue;
        }
        final start = edge.fromId == '*'
            ? (_positions[signal.publisher]?.clone() ?? edge.midpoint)
            : edge.start.clone();
        final pulse = SignalPulse(
          edge: edge,
          traceId: trace,
          start: start,
          end: edge.end.clone(),
        );
        pulses.add(pulse);
        added.add(pulse);
      }
    }
    return added;
  }

  /// Forgets a pulse that arrived.
  void arrived(SignalPulse pulse) => pulses.remove(pulse);
}

/// A pulse on screen: a dot in its trace's colour moving along its edge over [SignalPulses.travel]; a tap reports its key.
final class SignalPulseComponent extends CircleComponent with TapCallbacks {
  SignalPulseComponent(this.pulse, {this.onTap, this.onArrived})
    : super(
        radius: 6,
        anchor: Anchor.center,
        position: pulse.start.clone(),
        paint: ui.Paint()..color = pulse.color,
        priority: 10,
      );

  final SignalPulse pulse;
  final void Function(String key)? onTap;
  final void Function(SignalPulse pulse)? onArrived;

  @override
  Future<void> onLoad() async {
    await super.onLoad();
    final seconds =
        SignalPulses.travel.inMicroseconds / Duration.microsecondsPerSecond;
    await add(
      MoveEffect.to(
        pulse.end,
        EffectController(duration: seconds),
        onComplete: () {
          onArrived?.call(pulse);
          removeFromParent();
        },
      ),
    );
  }

  @override
  void onTapDown(TapDownEvent event) => onTap?.call(pulse.key);
}
