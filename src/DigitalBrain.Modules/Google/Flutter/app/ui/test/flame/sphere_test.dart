import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flame_test/flame_test.dart';
import 'package:flutter_test/flutter_test.dart';

SceneState scene(Map<String, Object?> sphere) => SceneState.fromJson({
  'name': 's',
  'revision': 1,
  'definition': {
    'nodes': [sphere],
    'camera': {'x': 0, 'y': 0, 'zoom': 1},
    'background': '#000000',
  },
});

Map<String, Object?> sphereNode({
  String blackboard = '{}',
  Map<String, Object?>? tree,
}) => {
  'id': 'ino',
  'type': 'sphere',
  'transform': {
    'x': 0,
    'y': 0,
    'z': 0,
    'scale': 1,
    'rotation': 0,
    'opacity': 1,
  },
  'style': null,
  'parameters': {'icon': 'ino', 'title': 'Ino', 'blackboard': blackboard},
  'children': [],
  'tweens': [],
  'inputs': ['tap'],
  'tree': tree,
};

void main() {
  final flameTester = FlameTester(SceneGame.new);

  flameTester.testGameWidget(
    'a sphere without a tree runs the default idle tree',
    setUp: (game, _) async {
      game.apply(scene(sphereNode()));
      await game.ready();
    },
    verify: (game, _) async {
      final sphere = game.componentFor('ino') as SphereComponent;
      expect(sphere.usesDefaultTree, isTrue);
      expect(sphere.treeRoot, isNotNull);
    },
  );

  flameTester.testGameWidget(
    'running=true makes the default tree pulse the body',
    setUp: (game, _) async {
      game.apply(scene(sphereNode(blackboard: '{"running":true}')));
      await game.ready();
    },
    verify: (game, _) async {
      final sphere = game.componentFor('ino') as SphereComponent;
      final scales = <double>[];
      for (var i = 0; i < 40; i++) {
        game.update(0.05);
        scales.add(sphere.body.scale.x);
      }
      expect(scales.reduce((a, b) => a > b ? a : b), greaterThan(1.05));
      final peak = scales.indexOf(scales.reduce((a, b) => a > b ? a : b));
      expect(scales.sublist(peak).any((s) => s < scales[peak] - 0.02), isTrue);
    },
  );

  flameTester.testGameWidget(
    'attention=true sets the badge flag and clears when the blackboard does',
    setUp: (game, _) async {
      game.apply(scene(sphereNode(blackboard: '{"attention":"true"}')));
      await game.ready();
    },
    verify: (game, _) async {
      final sphere = game.componentFor('ino') as SphereComponent;
      expect(sphere.attentionBadge, isTrue);
      game.apply(scene(sphereNode(blackboard: '{"attention":"false"}')));
      expect(sphere.attentionBadge, isFalse);
    },
  );

  flameTester.testGameWidget(
    'a tree carried by the node replaces the default and survives an identical apply',
    setUp: (game, _) async {
      game.apply(
        scene(
          sphereNode(
            blackboard: '{"mood":"idle"}',
            tree: {
              'root': {
                'kind': 'repeat',
                'children': [
                  {'kind': 'wait', 'children': [], 'seconds': 1},
                ],
              },
            },
          ),
        ),
      );
      await game.ready();
    },
    verify: (game, _) async {
      final sphere = game.componentFor('ino') as SphereComponent;
      expect(sphere.usesDefaultTree, isFalse);
      final root = sphere.treeRoot;
      game.apply(game.state!);
      expect(sphere.treeRoot, same(root));
    },
  );
}
