import 'dart:math' as math;

import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:flame/components.dart';
import 'package:flame/effects.dart';
import 'package:flame/game.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart';

import 'node_components.dart';
import 'placeholder_node.dart';
import 'prefabs/sphere.dart';
import 'primitive_nodes.dart';

typedef SceneInputHandler = void Function(
  String nodeId,
  String kind,
  double x,
  double y,
  String? payload,
);

/// Immutable snapshot of the active Flame scene camera (`x`, `y`, `zoom`).
@immutable
final class SceneCameraSnapshot {
  const SceneCameraSnapshot({
    this.x = 0,
    this.y = 0,
    this.zoom = SceneGame.defaultZoom,
  });

  final double x;
  final double y;
  final double zoom;

  int get zoomPercent => (zoom * 100).round();

  @override
  bool operator ==(Object other) =>
      other is SceneCameraSnapshot &&
      (other.x - x).abs() < 1e-4 &&
      (other.y - y).abs() < 1e-4 &&
      (other.zoom - zoom).abs() < 1e-4;

  @override
  int get hashCode => Object.hash(
    (x * 1000).round(),
    (y * 1000).round(),
    (zoom * 1000).round(),
  );
}

/// Draws a scene neuron: nodes become components keyed by id, a sphere runs the behavior tree its node
/// carries, and declared gestures come back through [onInput].
final class SceneGame extends FlameGame {
  SceneGame();

  static const double minZoom = 0.35;
  static const double maxZoom = 3.0;
  static const double defaultZoom = 1.0;
  static const double zoomStepFactor = 1.25;
  static const double keyboardPanStep = 56.0;

  final _byId = <String, NodeComponent>{};
  SceneInputHandler? onInput;
  SphereIconLoader? loadIcon;

  /// False under reduced motion: prefabs snap to their targets and stay still.
  bool animations = true;
  Duration appearanceDuration = const Duration(milliseconds: 200);
  final _appearances =
      <
        String,
        ({
          NodeComponent node,
          Vector2 target,
          double opacity,
          List<Effect> effects,
        })
      >{};
  SceneState? _state;
  SceneCamera? _appliedCamera;
  bool _userMovedCamera = false;
  Color _background = const Color(0xFF0B0F14);

  final ValueNotifier<SceneCameraSnapshot> _cameraState = ValueNotifier(
    const SceneCameraSnapshot(),
  );

  /// Reactive camera state observed by the floating HUD overlay in `SceneView`.
  ValueListenable<SceneCameraSnapshot> get cameraState => _cameraState;

  double get zoom => camera.viewfinder.zoom;
  Vector2 get cameraPosition => camera.viewfinder.position;

  SceneState? get state => _state;

  NodeComponent? componentFor(String nodeId) => _byId[nodeId];

  @override
  Color backgroundColor() => _background;

  void _syncCameraState() {
    final vf = camera.viewfinder;
    _cameraState.value = SceneCameraSnapshot(
      x: vf.position.x,
      y: vf.position.y,
      zoom: vf.zoom,
    );
  }

  /// Pans the camera in response to a screen-space pointer drag [screenDelta].
  void panByScreenDelta(Offset screenDelta) {
    if (screenDelta == Offset.zero) return;
    final currentZoom = camera.viewfinder.zoom.clamp(minZoom, maxZoom);
    camera.viewfinder.position -= Vector2(
      screenDelta.dx / currentZoom,
      screenDelta.dy / currentZoom,
    );
    _userMovedCamera = true;
    _syncCameraState();
    wake();
  }

  /// Pans the camera along world axes `(dx, dy)` in `[-1, 1]` scaled by [keyboardPanStep] / zoom (WASD).
  void panCameraDirection(double dx, double dy) {
    if (dx == 0 && dy == 0) return;
    final currentZoom = camera.viewfinder.zoom.clamp(minZoom, maxZoom);
    final step = keyboardPanStep / currentZoom;
    camera.viewfinder.position += Vector2(dx * step, dy * step);
    _userMovedCamera = true;
    _syncCameraState();
    wake();
  }

  /// Sets camera zoom clamped to `[minZoom, maxZoom]` (`0.35x`–`3.0x`), anchored at [focalPoint]
  /// in viewport coordinates (or viewport center when omitted).
  void setZoom(
    double targetZoom, {
    Offset? focalPoint,
    Size? viewportSize,
  }) {
    final oldZoom = camera.viewfinder.zoom.clamp(minZoom, maxZoom);
    final nextZoom = targetZoom.clamp(minZoom, maxZoom);
    final vw =
        viewportSize?.width ?? (hasLayout && canvasSize.x > 0 ? canvasSize.x : 0.0);
    final vh =
        viewportSize?.height ?? (hasLayout && canvasSize.y > 0 ? canvasSize.y : 0.0);

    if (focalPoint != null &&
        vw > 0 &&
        vh > 0 &&
        (nextZoom - oldZoom).abs() > 1e-6) {
      final offset = focalPoint - Offset(vw / 2, vh / 2);
      final camPos = camera.viewfinder.position;
      final worldAtFocal =
          camPos + Vector2(offset.dx / oldZoom, offset.dy / oldZoom);
      camera.viewfinder.position =
          worldAtFocal - Vector2(offset.dx / nextZoom, offset.dy / nextZoom);
    }

    camera.viewfinder.zoom = nextZoom;
    _userMovedCamera = true;
    _syncCameraState();
    wake();
  }

  /// Multiplies camera zoom by [factor], clamped to `[0.35, 3.0]`.
  void zoomBy(
    double factor, {
    Offset? focalPoint,
    Size? viewportSize,
  }) => setZoom(
    camera.viewfinder.zoom * factor,
    focalPoint: focalPoint,
    viewportSize: viewportSize,
  );

  void zoomIn({Offset? focalPoint, Size? viewportSize}) => zoomBy(
    zoomStepFactor,
    focalPoint: focalPoint,
    viewportSize: viewportSize,
  );

  void zoomOut({Offset? focalPoint, Size? viewportSize}) => zoomBy(
    1 / zoomStepFactor,
    focalPoint: focalPoint,
    viewportSize: viewportSize,
  );

  /// Resets the camera to `1.0x` zoom centered at `(0, 0)`.
  void resetCamera() {
    camera.viewfinder
      ..zoom = defaultZoom
      ..position = Vector2.zero();
    _userMovedCamera = true;
    _syncCameraState();
    wake();
  }

  Vector2 _worldPositionOf(NodeComponent component) {
    final pos = component.position.clone();
    var parent = component.parent;
    while (parent is NodeComponent) {
      pos.add(parent.position - parent.size / 2);
      parent = parent.parent;
    }
    return pos;
  }

  /// Computes the world-space bounding rectangle enclosing active scene nodes, or null when empty.
  Rect? sceneBounds() {
    if (_byId.isEmpty) return null;
    final candidates = _byId.values
        .where((c) => c is! GroupNode || c.size.x > 0 || c.size.y > 0)
        .toList();
    final source = candidates.isNotEmpty ? candidates : _byId.values.toList();
    if (source.isEmpty) return null;

    var minX = double.infinity;
    var minY = double.infinity;
    var maxX = double.negativeInfinity;
    var maxY = double.negativeInfinity;

    for (final node in source) {
      final pos = _worldPositionOf(node);
      final halfW = math.max(node.size.x * node.scale.x.abs() / 2, 16.0);
      final halfH = math.max(node.size.y * node.scale.y.abs() / 2, 16.0);
      final labelHeadroom = node is SphereComponent ? 28.0 : 0.0;
      minX = math.min(minX, pos.x - halfW);
      maxX = math.max(maxX, pos.x + halfW);
      minY = math.min(minY, pos.y - halfH - labelHeadroom);
      maxY = math.max(maxY, pos.y + halfH);
    }

    if (!minX.isFinite || !minY.isFinite || !maxX.isFinite || !maxY.isFinite) {
      return null;
    }
    return Rect.fromLTRB(minX, minY, maxX, maxY);
  }

  /// Centers the camera on the bounding box of active scene nodes and scales zoom to fit with [padding].
  void fitToGraph({double padding = 64.0, Size? viewportSize}) {
    final bounds = sceneBounds();
    if (bounds == null) {
      resetCamera();
      return;
    }
    final vw = viewportSize?.width ??
        (hasLayout && canvasSize.x > 0 ? canvasSize.x : 800.0);
    final vh = viewportSize?.height ??
        (hasLayout && canvasSize.y > 0 ? canvasSize.y : 500.0);
    final spanW = math.max(bounds.width + padding * 2, 1.0);
    final spanH = math.max(bounds.height + padding * 2, 1.0);
    final nextZoom = math.min(vw / spanW, vh / spanH).clamp(minZoom, maxZoom);

    camera.viewfinder
      ..position = Vector2(bounds.center.dx, bounds.center.dy)
      ..zoom = nextZoom;
    _userMovedCamera = true;
    _syncCameraState();
    wake();
  }

  /// True when [viewportPoint] hits a node that handles `'drag'` inputs, so background camera pan yields to node drag.
  bool hasDraggableAt(Offset viewportPoint, {Size? viewportSize}) {
    final vw =
        viewportSize?.width ?? (hasLayout && canvasSize.x > 0 ? canvasSize.x : 0.0);
    final vh =
        viewportSize?.height ?? (hasLayout && canvasSize.y > 0 ? canvasSize.y : 0.0);
    if (vw <= 0 || vh <= 0) return false;
    final currentZoom = camera.viewfinder.zoom.clamp(minZoom, maxZoom);
    final offset = viewportPoint - Offset(vw / 2, vh / 2);
    final worldPt = camera.viewfinder.position +
        Vector2(offset.dx / currentZoom, offset.dy / currentZoom);

    for (final comp in _byId.values) {
      if (!comp.draggable) continue;
      final center = _worldPositionOf(comp);
      final halfW = math.max(comp.size.x * comp.scale.x.abs() / 2, 4.0);
      final halfH = math.max(comp.size.y * comp.scale.y.abs() / 2, 4.0);
      if ((worldPt.x - center.x).abs() <= halfW &&
          (worldPt.y - center.y).abs() <= halfH) {
        return true;
      }
    }
    return false;
  }

  /// Diffs [state] against what is drawn: adds new nodes, removes those that left, resets the
  /// rest to their definition.
  void apply(SceneState state) {
    final initial = _state == null;
    _state = state;
    wake();
    final seen = <String>{};
    void walk(SceneNode definition, Component parent) {
      seen.add(definition.id);
      final node = definition;
      var component = _byId[node.id];
      if (component == null || component.typeName != node.type) {
        component?.removeFromParent();
        component = NodeComponents.build(node) ?? PlaceholderNode(node);
        _byId[node.id] = component;
        parent.add(component);
        if (!initial &&
            animations &&
            node.tweens.isEmpty &&
            appearanceDuration > Duration.zero) {
          final target = component.position.clone();
          final opacity = component.opacity;
          final seconds =
              appearanceDuration.inMicroseconds /
              Duration.microsecondsPerSecond;
          final effects = <Effect>[
            MoveEffect.to(target, EffectController(duration: seconds)),
            OpacityEffect.to(opacity, EffectController(duration: seconds)),
          ];
          component.position.y += 12;
          component.opacity = 0;
          component.addAll(effects);
          _appearances[node.id] = (
            node: component,
            target: target,
            opacity: opacity,
            effects: effects,
          );
        }
      } else {
        var appearance = _appearances[node.id];
        if (appearance != null &&
            (node.transform.x != appearance.target.x ||
                node.transform.y != appearance.target.y ||
                node.transform.opacity != appearance.opacity ||
                node.tweens.isNotEmpty)) {
          for (final effect in appearance.effects) {
            effect.removeFromParent();
          }
          _appearances.remove(node.id);
          appearance = null;
        }
        final position = appearance == null ? null : component.position.clone();
        final opacity = component.opacity;
        component.configure(node);
        if (position != null) {
          // Content can change while its entrance continues toward the same target.
          component.position.setFrom(position);
          component.opacity = opacity;
        }
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
      _appearances.remove(id);
    }

    final defCamera = state.definition.camera;
    final prevCamera = _appliedCamera;
    final cameraDefinitionChanged =
        prevCamera == null ||
        prevCamera.x != defCamera.x ||
        prevCamera.y != defCamera.y ||
        prevCamera.zoom != defCamera.zoom;
    if (initial || (cameraDefinitionChanged && !_userMovedCamera)) {
      _appliedCamera = defCamera;
      camera.viewfinder
        ..zoom = defCamera.zoom.clamp(minZoom, maxZoom)
        ..position = Vector2(defCamera.x, defCamera.y);
      _syncCameraState();
    }

    _background = sceneColor(state.definition.background) ?? _background;
    host();
  }

  /// Hands the host's motion preference and authenticated icon loader to its spheres.
  void host() {
    _appearances.removeWhere(
      (_, appearance) => appearance.effects.every((effect) => effect.isRemoved),
    );
    if (!animations) {
      for (final appearance in _appearances.values) {
        for (final effect in appearance.effects) {
          effect.removeFromParent();
        }
        appearance.node.position.setFrom(appearance.target);
        appearance.node.opacity = appearance.opacity;
      }
      _appearances.clear();
    }
    for (final component in _byId.values) {
      switch (component) {
        case SphereComponent sphere:
          sphere.animations = animations;
          sphere.loadIcon = loadIcon;
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
      (c) =>
          c is Effect ||
          c is ParticlesNode ||
          (c is SphereComponent && c.needsFrames),
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
