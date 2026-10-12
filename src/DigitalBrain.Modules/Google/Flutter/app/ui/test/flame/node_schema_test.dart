import 'dart:convert';
import 'dart:io';

import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('the Dart node schema matches docs/superpowers/contracts/flame-schema.json', () {
    final json = jsonDecode(File('${_repositoryRoot()}/docs/superpowers/contracts/flame-schema.json').readAsStringSync()) as Map<String, dynamic>;

    expect(FlameSchema.gestures, (json['gestures'] as List).cast<String>());
    expect(FlameSchema.tweenProperties, (json['tweenProperties'] as List).cast<String>());
    expect(FlameSchema.tweenCurves, (json['tweenCurves'] as List).cast<String>());

    final nodes = (json['nodes'] as List).cast<Map<String, dynamic>>();
    expect(FlameSchema.nodes.map((node) => node.type).toList(), nodes.map((node) => node['type']).toList());
    for (final node in FlameSchema.nodes) {
      final spec = nodes.firstWhere((candidate) => candidate['type'] == node.type);
      final parameters = (spec['parameters'] as List).cast<Map<String, dynamic>>();
      expect(node.parameters.map((param) => param.key).toList(), parameters.map((param) => param['key']).toList());
      for (var index = 0; index < node.parameters.length; index++) {
        final param = node.parameters[index];
        expect(param.kind.name, parameters[index]['kind']);
        expect(param.required, parameters[index]['required']);
        expect(param.choices, (parameters[index]['choices'] as List).cast<String>());
      }
    }
  });

  test('the mood enum is the schema mood domain', () {
    expect(InoMood.values.map((mood) => mood.name).toList(), FlameSchema.moods);
  });
}

String _repositoryRoot() {
  var directory = Directory.current;
  while (!File('${directory.path}/DigitalBrain.slnx').existsSync()) {
    final parent = directory.parent;
    if (parent.path == directory.path) {
      throw StateError('Repository root not found.');
    }
    directory = parent;
  }
  return directory.path;
}
