import 'dart:async';
import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter/src/generated/neurons.pb.dart' as pb;
import 'package:fixnum/fixnum.dart';
import 'package:test/test.dart';

void main() {
  test('an envelope decodes into a NeuronSignal with its JSON payload', () {
    final envelope = pb.SignalEnvelope(
      source: 'owner/apps/calculator/surface',
      type: 'SurfaceChanged',
      publisher: 'owner/apps/calculator/surface',
      sequence: Int64(3),
      payload: utf8.encode('{"name":"x","revision":3}'),
      serverTimeMs: Int64(1700000000000),
    );
    final signal = NeuronSignal.fromEnvelope(envelope);
    expect(signal.type, 'SurfaceChanged');
    expect(signal.sequence, 3);
    expect(signal.payload['revision'], 3);
    expect(signal.serverTimeMs, 1700000000000);
  });

  test('a call request carries the arguments as a JSON array', () {
    final request = NeuronChannel.callRequest('owner', 'A.IB', 'k', 'Read', [
      1,
      'two',
    ]);
    expect(utf8.decode(request.arguments), '[1,"two"]');
    expect(request.brainId, 'owner');
  });

  group('NeuronWatchMux', () {
    const a = WatchSourceSpec('C.IA', 'owner/a', ['AChanged']);
    const b = WatchSourceSpec('C.IB', 'owner/b', ['BChanged']);

    test('node watches share one Watch stream', () async {
      final link = FakeLink();
      final mux = NeuronWatchMux(() => link, 'owner', debounce: Duration.zero);
      addTearDown(mux.close);
      final seenA = <NeuronSignal>[], seenB = <NeuronSignal>[];
      final subA = mux.watch(a).listen(seenA.add);
      final subB = mux.watch(b).listen(seenB.add);
      await pump();
      expect(link.opened, hasLength(1));
      expect(link.opened.single.map((s) => s.key), ['owner/a', 'owner/b']);
      expect(mux.openStreams, 1);
      link.emit(signal('owner/b', 'BChanged'));
      link.emit(signal('owner/a', 'AChanged'));
      link.emit(signal('owner/a', 'Other'));
      await pump();
      expect(seenA.map((s) => s.type), ['AChanged']);
      expect(seenB.map((s) => s.type), ['BChanged']);
      await subB.cancel();
      await pump();
      expect(link.opened.last.map((s) => s.key), ['owner/a']);
      await subA.cancel();
      await pump();
      expect(mux.openStreams, 0);
    });

    test('a reopen drops replays already delivered', () async {
      final link = FakeLink();
      final mux = NeuronWatchMux(() => link, 'owner', debounce: Duration.zero);
      addTearDown(mux.close);
      final seen = <NeuronSignal>[];
      mux.watch(a).listen(seen.add);
      await pump();
      link.emit(signal('owner/a', 'AChanged', sequence: 7));
      await pump();
      mux.watch(b).listen((_) {});
      await pump();
      expect(link.opened, hasLength(2));
      link.emit(signal('owner/a', 'AChanged', sequence: 7));
      link.emit(signal('owner/a', 'AChanged'));
      await pump();
      expect(seen.map((s) => s.sequence), [7, 0]);
    });

    test('a broken stream reopens after the retry pause', () async {
      final link = FakeLink();
      final mux = NeuronWatchMux(
        () => link,
        'owner',
        debounce: Duration.zero,
        retryAfter: const Duration(milliseconds: 10),
      );
      addTearDown(mux.close);
      mux.watch(a).listen((_) {});
      await pump();
      link.fail();
      await Future<void>.delayed(const Duration(milliseconds: 40));
      expect(link.opened, hasLength(2));
    });

    test('more than 64 source signals split across streams', () async {
      final link = FakeLink();
      final mux = NeuronWatchMux(() => link, 'owner', debounce: Duration.zero);
      addTearDown(mux.close);
      for (var i = 0; i < 65; i++) {
        mux
            .watch(WatchSourceSpec('C.I', 'owner/n$i', const ['X']))
            .listen((_) {});
      }
      await pump();
      expect(link.opened.map((o) => o.length), [64, 1]);
    });
  });
}

Future<void> pump() => Future<void>.delayed(const Duration(milliseconds: 5));

NeuronSignal signal(String source, String type, {int sequence = 0}) =>
    NeuronSignal(
      source: source,
      type: type,
      publisher: source,
      sequence: sequence,
      payload: const {},
      serverTimeMs: 0,
    );

final class FakeLink implements NeuronLink {
  final opened = <List<WatchSourceSpec>>[];
  final _streams = <StreamController<NeuronSignal>>[];

  void emit(NeuronSignal signal) {
    for (final stream in _streams) {
      if (!stream.isClosed) stream.add(signal);
    }
  }

  void fail() => _streams.last.addError(StateError('broken'));

  @override
  Stream<NeuronSignal> watch(String brainId, List<WatchSourceSpec> sources) {
    opened.add(List.of(sources));
    final controller = StreamController<NeuronSignal>();
    controller.onCancel = () {
      _streams.remove(controller);
      return controller.close();
    };
    _streams.add(controller);
    return controller.stream;
  }

  @override
  Future<Map<String, dynamic>?> call(
    String brainId,
    String contract,
    String key,
    String method,
    List<Object?> arguments,
  ) async => null;

  @override
  Future<void> shutdown() async {}
}
