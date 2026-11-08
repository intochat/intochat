import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

Map<String, dynamic> button(String key) => {
  'kind': 'button',
  'key': key,
  'label': key,
  'emit': {'owner': 'brain', 'signal': 'focus'},
};

final sample = BrainSceneNode.fromJson({
  'kind': 'brainScene',
  'nodes': [
    {
      'id': 'ada/notes/default',
      'label': 'notes',
      'uri': 'neuron://ada/notes/default',
      'kind': 'unit',
      'x': 0,
      'y': 0,
      'z': 0,
      'select': button('node:ada/notes/default'),
    },
    {
      'id': 'draft:weather',
      'label': 'weather',
      'uri': 'neuron://draft:weather',
      'kind': 'draft',
      'x': 300,
      'y': 0,
      'z': 0,
      'status': 'building',
      'select': button('node:draft:weather'),
    },
  ],
  'synapses': [
    {'fromId': '*', 'toId': 'ada/notes/default', 'signalLabel': 'note.added'},
    {'fromId': '*', 'toId': 'gone', 'signalLabel': 'x'},
  ],
});

void main() {
  test('nodes become spheres and edges lines', () {
    final scene = brainSceneState(sample).definition.nodes;
    final spheres = scene.where((n) => n.type == 'sphere').toList();
    expect(spheres.map((n) => n.id), ['ada/notes/default', 'draft:weather']);
    expect(spheres.last.parameters['state'], 'unready');
    expect(spheres.last.transform.opacity, .5);
    final lines = scene.where((n) => n.type == 'path').toList();
    expect(lines.single.id, 'edge:*→ada/notes/default:note.added');
  });

  testWidgets('a tap on a node, an edge or a pulse emits its key', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    final tail = StreamController<List<JournalTailSignal>>();
    final ctx = NodeContext(
      view: 'local/apps/creator/view',
      send: (event) async {
        events.add(event);
        return null;
      },
    );
    await tester.pumpWidget(
      MaterialApp(
        home: SizedBox(
          height: 400,
          child: BrainSceneView(node: sample, ctx: ctx, signals: tail.stream),
        ),
      ),
    );
    await tester.pump();
    final state = tester.state<BrainSceneViewState>(
      find.byType(BrainSceneView),
    );
    state.game.simulateTap('ada/notes/default');
    state.game.simulateTap('edge:*→ada/notes/default:note.added');
    tail.add([
      JournalTailSignal(
        seq: 1,
        publisher: 'ada/x/default',
        alias: 'note.added',
        at: DateTime.utc(2026),
        traceId: 'tr',
      ),
    ]);
    await tester.pump();
    expect(state.pulses.pulses, hasLength(1));
    final pulse = state.game.world.children
        .whereType<SignalPulseComponent>()
        .single;
    pulse.onTap!(pulse.pulse.key);
    await tester.pump();
    expect(events.map((e) => e['key']), [
      'node:ada/notes/default',
      'edge:*→ada/notes/default:note.added',
      'pulse:tr',
    ]);
    await tester.pump(const Duration(seconds: 1));
    await tail.close();
    await tester.pumpWidget(const SizedBox());
  });
}
