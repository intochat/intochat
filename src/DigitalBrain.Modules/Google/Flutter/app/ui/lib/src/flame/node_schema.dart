/// The scene node vocabulary as one schema, mirroring the Flame contracts' `FlameSchema`. The canonical
/// artifact is `docs/superpowers/contracts/flame-schema.json`; `node_schema_test.dart` fails if this mirror
/// drifts from it, and the contracts' `SchemaParityFacts` fails if the C# side drifts from the same file.
library;

enum FlameParamKind { text, number, color, json, choice, boolean }

final class FlameParam {
  const FlameParam(this.key, this.kind, {this.required = false, this.choices = const []});

  final String key;
  final FlameParamKind kind;
  final bool required;
  final List<String> choices;
}

final class FlameNodeType {
  const FlameNodeType(this.type, this.parameters);

  final String type;
  final List<FlameParam> parameters;

  FlameParam? param(String key) {
    for (final param in parameters) {
      if (param.key == key) return param;
    }
    return null;
  }
}

abstract final class FlameSchema {
  static const List<String> gestures = ['tap', 'drag', 'hover'];
  static const List<String> tweenProperties = ['x', 'y', 'scale', 'rotation', 'opacity'];
  static const List<String> tweenCurves = ['linear', 'easeIn', 'easeOut', 'easeInOut'];
  static const List<String> moods = ['idle', 'listening', 'thinking', 'acting', 'speaking', 'error', 'disconnected'];

  static const List<FlameNodeType> nodes = [
    FlameNodeType('circle', [
      FlameParam('radius', FlameParamKind.number),
      FlameParam('color', FlameParamKind.color),
    ]),
    FlameNodeType('rect', [
      FlameParam('width', FlameParamKind.number),
      FlameParam('height', FlameParamKind.number),
      FlameParam('color', FlameParamKind.color),
      FlameParam('corner', FlameParamKind.number),
    ]),
    FlameNodeType('text', [
      FlameParam('text', FlameParamKind.text, required: true),
      FlameParam('size', FlameParamKind.number),
      FlameParam('color', FlameParamKind.color),
      FlameParam('align', FlameParamKind.choice, choices: ['left', 'center', 'right']),
    ]),
    FlameNodeType('sprite', [
      FlameParam('src', FlameParamKind.text, required: true),
      FlameParam('width', FlameParamKind.number),
      FlameParam('height', FlameParamKind.number),
    ]),
    FlameNodeType('path', [
      FlameParam('d', FlameParamKind.text, required: true),
    ]),
    FlameNodeType('particles', [
      FlameParam('count', FlameParamKind.number),
      FlameParam('speed', FlameParamKind.number),
      FlameParam('color', FlameParamKind.color),
    ]),
    FlameNodeType('group', []),
    FlameNodeType('ino_sphere', [
      FlameParam('mood', FlameParamKind.choice, choices: moods),
      FlameParam('radius', FlameParamKind.number),
      FlameParam('hue', FlameParamKind.number),
    ]),
    FlameNodeType('brain_graph', [
      FlameParam('source', FlameParamKind.choice, choices: ['events', 'static']),
      FlameParam('filter', FlameParamKind.choice, choices: ['apps', 'all']),
      FlameParam('selected', FlameParamKind.text),
      FlameParam('layout', FlameParamKind.choice, choices: ['sphere']),
      FlameParam('title', FlameParamKind.text),
    ]),
    FlameNodeType('activity_ring', [
      FlameParam('items', FlameParamKind.json),
      FlameParam('radius', FlameParamKind.number),
    ]),
  ];

  static FlameNodeType? forType(String type) {
    for (final node in nodes) {
      if (node.type == type) return node;
    }
    return null;
  }
}
