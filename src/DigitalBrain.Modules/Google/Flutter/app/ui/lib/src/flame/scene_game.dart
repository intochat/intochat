import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flame/components.dart';
import 'package:flame/effects.dart';
import 'package:flame/game.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart';

import 'node_components.dart';
import 'placeholder_node.dart';
import 'prefabs/brain_graph.dart';
import 'prefabs/ino_sphere.dart';
import 'primitive_nodes.dart';

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

  /// The host's brain-events stream, handed to every `brain_graph` node with `source=events`.
  WatchBrain? watchBrain;

  /// False under reduced motion: prefabs snap to their targets and stay still.
  bool animations = true;
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
    final patches = <String, List<NodePatch>>{};
    for (final machine in state.definition.machines) {
      if (machine.states.isEmpty) continue;
      final current = state.current[machine.id] ?? machine.initial;
      final def = machine.states.firstWhere(
        (s) => s.name == current,
        orElse: () => machine.states.first,
      );
      for (final patch in def.apply) {
        (patches[patch.nodeId] ??= []).add(patch);
      }
    }
    final seen = <String>{};
    void walk(SceneNode definition, Component parent) {
      seen.add(definition.id);
      final node = (patches[definition.id] ?? const <NodePatch>[]).fold(
        definition,
        NodeComponent.withPatch,
      );
      var component = _byId[node.id];
      if (component == null || component.typeName != node.type) {
        component?.removeFromParent();
        component = NodeComponents.build(node) ?? PlaceholderNode(node);
        _byId[node.id] = component;
        parent.add(component);
      } else {
        component.configure(node);
        // A parent rebuilt for a new type, or a node moved in the definition, takes it along.
        if (component.parent != parent) component.parent = parent;
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
    final camera = state.definition.camera;
    this.camera.viewfinder
      ..zoom = camera.zoom
      ..position = Vector2(camera.x, camera.y);
    _background = sceneColor(state.definition.background) ?? _background;
    host();
  }

  /// Hands [watchBrain] and [animations] to the prefabs that need them.
  void host() {
    for (final component in _byId.values) {
      switch (component) {
        case BrainGraph graph:
          graph.animations = animations;
          if (graph.watch != watchBrain) graph.watch = watchBrain;
        case InoSphere sphere:
          sphere.animations = animations;
        default:
      }
    }
    wake();
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
