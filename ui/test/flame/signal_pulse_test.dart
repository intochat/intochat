import 'package:flame/game.dart';
import 'package:flame_test/flame_test.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

JournalTailSignal signal(
  String alias, {
  String publisher = 'ada/x/default',
  String trace = 't1',
  int seq = 1,
}) => JournalTailSignal(
  seq: seq,
  publisher: publisher,
  alias: alias,
  at: DateTime.utc(2026),
  traceId: trace,
);

SignalPulses field({int cap = 64}) => SignalPulses(cap: cap)
  ..layout(
    [
      PulseEdge(
        key: 'edge:*→u:note.added',
        fromId: '*',
        toId: 'u',
        alias: 'note.added',
        start: Vector2(0, -90),
        end: Vector2(0, 0),
      ),
      PulseEdge(
        key: 'edge:*→v:other',
        fromId: '*',
        toId: 'v',
        alias: 'other',
        start: Vector2(100, -90),
        end: Vector2(100, 0),
      ),
    ],
    {
      'u': Vector2(0, 0),
      'v': Vector2(100, 0),
      'ada/x/default': Vector2(-50, 20),
    },
  );

void main() {
  test('a signal spawns a pulse on the matching edge', () {
    final pulses = field();
    final added = pulses.feed([signal('note.added')]);
    final pulse = added.single;
    expect(pulse.edge.key, 'edge:*→u:note.added');
    expect(pulse.key, 'pulse:t1');
    expect(
      pulse.start,
      Vector2(-50, 20),
      reason: 'a * source starts at its publisher',
    );
    expect(pulse.end, Vector2(0, 0));
    expect(pulseColor('t1'), pulseColor('t1'));
    expect(pulseColor('t1'), isNot(pulseColor('t2')));
    final unknown = pulses.feed([
      signal('note.added', publisher: 'elsewhere', trace: 't9'),
    ]).single;
    expect(
      unknown.start,
      Vector2(0, -45),
      reason: 'an unknown publisher starts at the midpoint',
    );
  });

  test('the cap holds and a burst of one trace coalesces', () {
    final pulses = field(cap: 3);
    pulses.feed([for (var i = 0; i < 5; i++) signal('note.added', seq: i)]);
    expect(pulses.pulses, hasLength(1));
    pulses.feed([
      for (var i = 0; i < 10; i++) signal('note.added', trace: 'x$i', seq: i),
    ]);
    expect(pulses.pulses, hasLength(3));
    pulses.arrived(pulses.pulses.first);
    expect(pulses.pulses, hasLength(2));
  });

  test('a signal with no matching edge spawns nothing', () {
    final pulses = field();
    expect(pulses.feed([signal('nobody.listens')]), isEmpty);
    expect(pulses.pulses, isEmpty);
  });

  test('one alias on two edges spawns two pulses; a fixed source starts at its node', () {
    final pulses = SignalPulses()
      ..layout(
        [
          PulseEdge(
            key: 'a',
            fromId: 'p',
            toId: 'u',
            alias: 's',
            start: Vector2(5, 5),
            end: Vector2(0, 0),
          ),
          PulseEdge(
            key: 'b',
            fromId: '*',
            toId: 'v',
            alias: 's',
            start: Vector2(100, -90),
            end: Vector2(100, 0),
          ),
        ],
        {'u': Vector2(0, 0), 'v': Vector2(100, 0), 'p': Vector2(5, 5)},
      );
    final added = pulses.feed([signal('s', publisher: 'q')]);
    expect(added.map((p) => p.edge.key), ['a', 'b']);
    expect(added.first.start, Vector2(5, 5));
  });

  test('a signal without a trace keys its pulse by seq', () {
    final pulses = field();
    final pulse = pulses.feed([
      JournalTailSignal(
        seq: 7,
        publisher: 'p',
        alias: 'note.added',
        at: DateTime.utc(2026),
      ),
    ]).single;
    expect(pulse.key, 'pulse:seq:7');
  });

  test('a pulse expires by its age and frees the cap', () {
    final pulses = field(cap: 1);
    pulses.feed([signal('note.added')]);
    expect(pulses.feed([signal('note.added', trace: 't2')]), isEmpty);
    pulses.advance(.6);
    expect(pulses.pulses, hasLength(1), reason: 'not yet twice the travel');
    pulses.advance(.7);
    expect(pulses.pulses, isEmpty);
    expect(pulses.feed([signal('note.added', trace: 't2')]), hasLength(1));
  });

  test('a new layout drops the pulses in flight', () {
    final pulses = field()..feed([signal('note.added')]);
    pulses.layout(const [], const {});
    expect(pulses.pulses, isEmpty);
  });

  final flame = FlameTester(FlameGame.new);
  flame.testGameWidget(
    'a pulse component travels its edge and reports its arrival',
    setUp: (game, _) async {
      final pulses = field();
      final pulse = pulses.feed([signal('note.added')]).single;
      final component = SignalPulseComponent(pulse, onArrived: pulses.arrived);
      await game.world.add(component);
      await game.ready();
      game.update(.3);
      expect(component.position.distanceTo(pulse.end), greaterThan(0));
      game.update(.4);
      await game.ready();
      expect(pulses.pulses, isEmpty);
      expect(component.isMounted, isFalse);
    },
  );

  test('replaysInOrder', () {
    final pulses = field();
    final steps = [
      const ReplayStep(
        index: 0,
        seq: 1,
        alias: 'note.added',
        from: 'ada/x/default',
        to: 'u',
        traceId: 't1',
      ),
      const ReplayStep(
        index: 1,
        seq: 2,
        alias: 'other',
        from: 'ada/x/default',
        to: '',
        traceId: 't1',
      ),
      const ReplayStep(
        index: 2,
        seq: 3,
        alias: 'note.added',
        from: 'ada/x/default',
        to: 'u',
        traceId: 't1',
      ),
    ];
    pulses.feed([signal('note.added', trace: 'live')]);
    expect(pulses.pulses, hasLength(1));

    // Entering the replay drops the live pulses; each step spawns its own pulse, in order, keyed by the trace.
    final order = <String>[];
    for (var i = 0; i < steps.length; i++) {
      final added = pulses.replay(steps, i);
      expect(added, hasLength(1));
      order.add(added.single.edge.alias);
      expect(added.single.key, 'pulse:t1');
      pulses.advance(.7);
    }
    expect(order, ['note.added', 'other', 'note.added']);
    expect(pulses.replaying, isTrue);
    expect(
      pulses.replay(steps, 2),
      isEmpty,
      reason: 'the same step replays once',
    );

    // Live pulses are paused while a step is current...
    expect(pulses.feed([signal('note.added', trace: 'live')]), isEmpty);
    // ...and resume when the replay ends.
    pulses.replay(steps, null);
    expect(pulses.replaying, isFalse);
    expect(pulses.feed([signal('note.added', trace: 'live')]), hasLength(1));
  });
}
