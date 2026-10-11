import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flame_test/flame_test.dart';
import 'package:flutter_test/flutter_test.dart';

SceneState scene(
  List<Map<String, Object?>> nodes, {
  Map<String, String> current = const {},
  List<Map<String, Object?>> machines = const [],
}) => SceneState.fromJson({
  'name': 's',
  'revision': 1,
  'current': current,
  'definition': {
    'nodes': nodes,
    'machines': machines,
    'camera': {'x': 0, 'y': 0, 'zoom': 1},
    'background': '#000000',
  },
});

Map<String, Object?> node(
  String id,
  String type, {
  Map<String, String> params = const {},
  List<String> inputs = const [],
}) => {
  'id': id,
  'type': type,
  'transform': {
    'x': 10,
    'y': 10,
    'z': 0,
    'scale': 1,
    'rotation': 0,
    'opacity': 1,
  },
  'style': '#ffffff',
  'parameters': params,
  'children': [],
  'tweens': [],
  'inputs': inputs,
};

void main() {
  final flameTester = FlameTester(SceneGame.new);

  flameTester.testGameWidget(
    'primitive nodes become components keyed by id',
    setUp: (game, _) async {
      game.apply(
        scene([
          node('a', 'circle'),
          node('b', 'rect'),
          node('c', 'text', params: {'text': 'hi'}),
        ]),
      );
      await game.ready();
    },
    verify: (game, _) async {
      expect(game.componentFor('a'), isNotNull);
      expect(game.componentFor('b'), isNotNull);
      expect(game.componentFor('c'), isNotNull);
    },
  );

  flameTester.testGameWidget(
    'an unknown node type renders a placeholder and the siblings',
    setUp: (game, _) async {
      game.apply(scene([node('a', 'circle'), node('x', 'hologram')]));
      await game.ready();
    },
    verify: (game, _) async {
      expect(game.componentFor('a'), isNotNull);
      expect(game.componentFor('x'), isA<PlaceholderNode>());
    },
  );

  flameTester.testGameWidget(
    'a later apply removes nodes that left and patches the current state',
    setUp: (game, _) async {
      game.apply(scene([node('a', 'circle'), node('b', 'rect')]));
      await game.ready();
      game.apply(
        scene(
          [
            node('a', 'circle', params: {'radius': '40'}),
          ],
          machines: [
            {
              'id': 'm',
              'initial': 's1',
              'states': [
                {'name': 's1', 'apply': []},
                {
                  'name': 's2',
                  'apply': [
                    {
                      'nodeId': 'a',
                      'style': '#ff0000',
                      'parameters': null,
                      'transform': null,
                      'tweens': null,
                    },
                  ],
                },
              ],
              'transitions': [],
            },
          ],
          current: {'m': 's2'},
        ),
      );
      await game.ready();
    },
    verify: (game, _) async {
      expect(game.componentFor('b'), isNull);
      expect(game.componentFor('a')!.style, '#ff0000');
    },
  );

  flameTester.testGameWidget(
    'a tap on a node with the tap input reports it',
    setUp: (game, _) async {
      game.apply(
        scene([
          node('a', 'circle', inputs: ['tap']),
        ]),
      );
      await game.ready();
    },
    verify: (game, tester) async {
      final reports = <String>[];
      game.onInput = (nodeId, kind, x, y, payload) =>
          reports.add('$nodeId:$kind');
      game.simulateTap('a');
      expect(reports, ['a:tap']);
    },
  );
}
