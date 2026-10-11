import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flame_test/flame_test.dart';
import 'package:flutter_test/flutter_test.dart';

import 'scene_game_test.dart' show node, scene;

void main() {
  final flameTester = FlameTester(SceneGame.new);

  flameTester.testGameWidget(
    'ino_sphere changes its look with mood',
    setUp: (game, _) async {
      game.apply(
        scene([
          node('ino', 'ino_sphere', params: {'mood': 'idle'}),
        ]),
      );
      await game.ready();
    },
    verify: (game, _) async {
      final sphere = game.componentFor('ino') as InoSphere;
      game.update(2);
      final idle = sphere.glow;
      final definition = game.state!.definition.nodes.single;
      sphere.configure(
        NodeComponent.withPatch(
          definition,
          const NodePatch(nodeId: 'ino', parameters: {'mood': 'thinking'}),
        ),
      );
      game.update(0.5);
      expect(sphere.glow, isNot(idle));
      expect(sphere.mood, InoMood.thinking);
    },
  );

  flameTester.testGameWidget(
    'activity_ring lays out one tappable child per item and reports its id',
    setUp: (game, _) async {
      game.apply(
        scene([
          node(
            'ring',
            'activity_ring',
            params: {
              'items': '[{"id":"r1","label":"Routing","kind":"route","progress":0.4},{"id":"b2","label":"timer","kind":"behavior","progress":1}]',
            },
          ),
        ]),
      );
      await game.ready();
    },
    verify: (game, _) async {
      final ring = game.componentFor('ring') as ActivityRing;
      expect(ring.items.length, 2);
      final reports = <String?>[];
      game.onInput = (id, kind, x, y, payload) => reports.add(payload);
      ring.simulateTap('b2');
      expect(reports, ['b2']);
    },
  );

  flameTester.testGameWidget(
    'brain_graph projects a static snapshot into nodes with depth and pulses activity along edges',
    setUp: (game, _) async {
      game.apply(
        scene([
          node(
            'brain',
            'brain_graph',
            params: {'source': 'static', 'filter': 'all'},
          ),
        ]),
      );
      await game.ready();
      final graph = game.componentFor('brain') as BrainGraph;
      graph.show(
        BrainSnapshot.fromJson({
          'rootId': 'owner',
          'scope': 'owner',
          'observedAt': '2026-10-11T00:00:00Z',
          'truncated': false,
          'nodes': [
            {
              'id': 'a',
              'type': 'app',
              'name': 'ino',
              'label': 'Ino',
              'module': 'apps',
              'role': 'app',
              'status': 'active',
            },
            {
              'id': 'b',
              'type': 'router',
              'name': 'router',
              'label': 'Router',
              'module': 'ai',
              'role': 'kernel',
              'status': 'active',
            },
          ],
          'synapses': [
            {
              'id': 'a-b',
              'sourceId': 'a',
              'targetId': 'b',
              'signalType': 'Routed',
              'kind': 'subscription',
            },
          ],
          'activity': [
            {
              'id': 'e1',
              'neuronId': 'b',
              'direction': 'in',
              'sequence': 1,
              'signalType': 'Routed',
              'operationId': 'op1',
              'kind': 'BehaviorRan',
              'timestamp': '2026-10-11T00:00:01Z',
            },
          ],
          'correlations': [],
        }),
      );
      game.update(0.1);
    },
    verify: (game, _) async {
      final graph = game.componentFor('brain') as BrainGraph;
      expect(graph.nodeCount, 2);
      expect(graph.edgeCount, 1);
      expect(graph.pulses, isNotEmpty);
      final depths = graph.projectedDepths();
      expect(depths.values.every((d) => d >= -1 && d <= 1), isTrue);
    },
  );
}
