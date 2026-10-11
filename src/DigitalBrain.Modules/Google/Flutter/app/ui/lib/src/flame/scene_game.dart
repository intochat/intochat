import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flame/components.dart';
import 'package:flame/effects.dart';
import 'package:flame/game.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart';

import 'node_components.dart';

typedef SceneInputHandler = void Function(
  String nodeId,
  String kind,
  double x,
  double y,
  String? payload,
);

/// Draws a scene neuron: nodes become components keyed by id, the machines' current states
/// patch them, and declared gestures come back through [onInput].
final class SceneGame extends FlameGame {
  SceneGame();

  final _byId = <String, NodeComponent>{};
  SceneInputHandler? onInput;
  SceneState? _state;
  Color _background = const Color(0xFF0B0F14);

  SceneState? get state => _state;

  NodeComponent? componentFor(String nodeId) => _byId[nodeId];

  @override
  Color backgroundColor() => _background;

  /// Diffs [state] against what is drawn: adds new nodes, removes those that left, resets the
  /// rest to their definition, then applies every machine's current state in machine order.
  void apply(SceneState state) {
    _state = state;
    wake();
    final seen = <String>{};
    void walk(SceneNode node, Component parent) {
      seen.add(node.id);
      var component = _byId[node.id];
      if (component == null || component.typeName != node.type) {
        component?.removeFromParent();
        component = NodeComponents.build(node) ?? PlaceholderNode(node);
        _byId[node.id] = component;
        parent.add(component);
      } else {
        component.configure(node);
      }
      for (final child in node.children) {
        walk(child, component);
      }
    }

    for (final node in state.definition.nodes) {
      walk(node, world);
    }
    for (final id in _byId.keys.where((k) => !seen.contains(k)).toList()) {
      _byId.remove(id)!.removeFromParent();
    }
    for (final machine in state.definition.machines) {
      if (machine.states.isEmpty) continue;
      final current = state.current[machine.id] ?? machine.initial;
      final def = machine.states.firstWhere(
        (s) => s.name == current,
        orElse: () => machine.states.first,
      );
      for (final patch in def.apply) {
        _byId[patch.nodeId]?.patch(patch);
      }
    }
    final camera = state.definition.camera;
    this.camera.viewfinder
      ..zoom = camera.zoom
      ..position = Vector2(camera.x, camera.y);
    _background = sceneColor(state.definition.background) ?? _background;
  }

  int _idleFrames = 0;

  /// Restarts the loop after it stopped on a still scene.
  void wake() {
    _idleFrames = 0;
    if (paused) resumeEngine();
  }

  // A scene with no running effect or particles stops its loop, so a still scene costs no frames
  // (and widget tests can settle); apply, input and asset loads wake it.
  @override
  void update(double dt) {
    super.update(dt);
    final animating = world.descendants().any(
      (c) => c is Effect || c is ParticlesNode,
    );
    _idleFrames = animating ? 0 : _idleFrames + 1;
    if (_idleFrames > 2) pauseEngine();
  }

  void report(
    NodeComponent component,
    String kind,
    Vector2 at, [
    String? payload,
  ]) => onInput?.call(component.nodeId, kind, at.x, at.y, payload);

  @visibleForTesting
  void simulateTap(String nodeId) {
    final component = _byId[nodeId];
    if (component != null && component.inputs.contains('tap')) {
      report(component, 'tap', component.position);
    }
  }
}
