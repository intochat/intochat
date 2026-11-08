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

/// One step of a scripted replay: the signal [alias] of [traceId] at journal [seq], from the publisher [from] towards [to]
/// (empty: any edge of the alias). [index] is its place in the sequence.
final class ReplayStep {
  const ReplayStep({
    required this.index,
    required this.seq,
    required this.alias,
    required this.from,
    required this.to,
    required this.traceId,
  });
  final int index, seq;
  final String alias, from, to, traceId;
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
/// at most [cap] pulses fly at once, a signal whose trace already crosses the same edge joins it instead of spawning, and a
/// pulse older than twice [travel] is dropped by [advance] even if its arrival was never reported.
final class SignalPulses {
  SignalPulses({this.cap = 64});

  static const travel = Duration(milliseconds: 600);
  static final double _expiry =
      2 * travel.inMicroseconds / Duration.microsecondsPerSecond;
  final int cap;
  final _byAlias = <String, List<PulseEdge>>{};
  final _positions = <String, Vector2>{};
  final pulses = <SignalPulse>[];
  int? _replayAt;

  /// True while a replay step is current: live signals are not drawn until [replay] is called with no step.
  bool get replaying => _replayAt != null;

  /// Plays the replay of [steps] at step [at], one pulse per edge of that step's signal, in order as the cursor moves. Entering
  /// a replay drops the live pulses in flight; a null [at] ends it, and live signals are drawn again. Returns the pulses added.
  List<SignalPulse> replay(List<ReplayStep> steps, int? at) {
    final wasReplaying = replaying;
    if (at == null || at < 0 || at >= steps.length) {
      _replayAt = null;
      if (wasReplaying) pulses.clear();
      return const [];
    }
    if (!wasReplaying) pulses.clear();
    if (_replayAt == at) return const [];
    _replayAt = at;
    final step = steps[at];
    final added = <SignalPulse>[];
    for (final edge in _byAlias[step.alias] ?? const <PulseEdge>[]) {
      if (step.to.isNotEmpty && edge.toId != step.to) continue;
      if (pulses.length >= cap) break;
      final start = edge.fromId == '*'
          ? (_positions[step.from]?.clone() ?? edge.midpoint)
          : edge.start.clone();
      final pulse = SignalPulse(
        edge: edge,
        traceId: step.traceId,
        start: start,
        end: edge.end.clone(),
      );
      pulses.add(pulse);
      added.add(pulse);
    }
    return added;
  }

  /// Replaces the drawn edges and node positions (the scene changed); pulses in flight are dropped.
  void layout(Iterable<PulseEdge> edges, Map<String, Vector2> positions) {
    pulses.clear();
    _replayAt = null;
    _byAlias.clear();
    _positions
      ..clear()
      ..addAll(positions);
    for (final edge in edges) {
      if (!_positions.containsKey(edge.toId)) continue;
      (_byAlias[edge.alias] ??= []).add(edge);
    }
  }

  /// Spawns the pulses of [signals]; returns those it added.
  List<SignalPulse> feed(Iterable<JournalTailSignal> signals) {
    final added = <SignalPulse>[];
    if (replaying) return added;
    for (final signal in signals) {
      final trace = signal.traceId ?? 'seq:${signal.seq}';
      for (final edge in _byAlias[signal.alias] ?? const <PulseEdge>[]) {
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

  /// Ages every pulse by [dt] seconds and drops those past the expiry.
  void advance(double dt) {
    for (final pulse in pulses) {
      pulse.elapsed += dt;
    }
    pulses.removeWhere((pulse) => pulse.elapsed >= _expiry);
  }

  /// Forgets a pulse that arrived.
  void arrived(SignalPulse pulse) => pulses.remove(pulse);

  void clear() => pulses.clear();
}

/// Ages a [SignalPulses] with the game clock.
final class SignalPulseClock extends Component {
  SignalPulseClock(this.pulses);
  final SignalPulses pulses;

  @override
  void update(double dt) => pulses.advance(dt);
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
