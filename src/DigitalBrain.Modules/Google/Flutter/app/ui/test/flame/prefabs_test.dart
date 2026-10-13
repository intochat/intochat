import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flame/components.dart' show Vector2;
import 'package:flame_test/flame_test.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter_test/flutter_test.dart';

import 'scene_game_test.dart' show node, scene;

const _items =
    '[{"id":"r1","label":"Routing","kind":"route","progress":0.4},{"id":"b2","label":"timer","kind":"behavior","progress":1}]';

Map<String, Object?> _atOrigin(Map<String, Object?> node) => {
  ...node,
  'transform': {
    'x': 0,
    'y': 0,
    'z': 0,
    'scale': 1,
    'rotation': 0,
    'opacity': 1,
  },
};

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
        SceneNode(
          id: definition.id,
          type: definition.type,
          transform: definition.transform,
          style: definition.style,
          parameters: {...definition.parameters, 'mood': 'thinking'},
          inputs: definition.inputs,
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
            inputs: ['tap'],
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

  flameTester.testGameWidget(
    'activity_ring without the tap input reports nothing',
    setUp: (game, _) async {
      game.apply(
        scene([
          node('ring', 'activity_ring', params: {'items': _items}),
        ]),
      );
      await game.ready();
    },
    verify: (game, _) async {
      final ring = game.componentFor('ring') as ActivityRing;
      final reports = <String?>[];
      game.onInput = (id, kind, x, y, payload) => reports.add(payload);
      ring.simulateTap('b2');
      expect(reports, isEmpty);
    },
  );

  flameTester.testGameWidget(
    'a real tap on a ring pill reports its id',
    setUp: (game, _) async {
      game.apply(
        scene([
          _atOrigin(
            node(
              'ring',
              'activity_ring',
              params: {'items': _items, 'radius': '120'},
              inputs: ['tap'],
            ),
          ),
        ]),
      );
      await game.ready();
    },
    verify: (game, tester) async {
      for (var i = 0; i < 90; i++) {
        await tester.pump(const Duration(milliseconds: 16));
      }
      final ring = game.componentFor('ring') as ActivityRing;
      final pill = ring.items.firstWhere((i) => i.entry.id == 'b2');
      final reports = <String?>[];
      game.onInput = (id, kind, x, y, payload) =>
          reports.add('$id:$kind:$payload');
      // The pill's outer edge: outside a box of just 2*radius.
      final at = game.canvasSize / 2 + pill.absoluteCenter + Vector2(0, 10);
      await tester.tapAt(Offset(at.x, at.y));
      // Let the tap recognizer's double-tap window close.
      await tester.pump(const Duration(milliseconds: 500));
      expect(reports, ['ring:tap:b2']);
    },
  );

  flameTester.testGameWidget(
    'a drag on the brain graph rotates it and reports nothing',
    setUp: (game, _) async {
      game.apply(
        scene([
          _atOrigin(
            node(
              'brain',
              'brain_graph',
              params: {'source': 'static'},
              inputs: ['tap', 'drag'],
            ),
          ),
        ]),
      );
      await game.ready();
    },
    verify: (game, tester) async {
      final graph = game.componentFor('brain') as BrainGraph;
      final reports = <String>[];
      game.onInput = (id, kind, x, y, payload) => reports.add('$id:$kind');
      final before = graph.yaw;
      final center = game.canvasSize / 2;
      final gesture = await tester.startGesture(
        Offset(center.x + 40, center.y + 40),
        kind: PointerDeviceKind.mouse,
      );
      await gesture.moveBy(const Offset(30, 0));
      await tester.pump(const Duration(milliseconds: 16));
      await gesture.moveBy(const Offset(30, 0));
      await tester.pump(const Duration(milliseconds: 16));
      await gesture.up();
      await tester.pump(const Duration(milliseconds: 16));
      expect(graph.yaw, isNot(before));
      expect(graph.position.x, 0);
      expect(reports, isEmpty);
    },
  );
}
