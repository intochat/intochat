import 'dart:convert';
import 'dart:io';

import 'package:collection/collection.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:test/test.dart';

Directory _wireDir() {
  var dir = Directory.current.absolute;
  while (true) {
    final candidate = Directory('${dir.path}/src/Assets/wire');
    if (candidate.existsSync()) return candidate;
    final parent = dir.parent;
    if (parent.path == dir.path) {
      throw StateError('src/Assets/wire not found above ${Directory.current}');
    }
    dir = parent;
  }
}

Map<String, dynamic> _sample(String file) =>
    jsonDecode(File('${_wireDir().path}/$file').readAsStringSync())
        as Map<String, dynamic>;

void _roundTrips(
  String file,
  Map<String, dynamic> Function(Map<String, dynamic>) through,
) {
  test('$file round-trips', () {
    final json = _sample(file);
    final back = through(json);
    expect(
      const DeepCollectionEquality().equals(back, json),
      isTrue,
      reason: 'expected $json but got $back',
    );
  });
}

void main() {
  _roundTrips('presence.json', (j) => Presence.fromJson(j).toJson());
  _roundTrips('focus.json', (j) => Focus.fromJson(j).toJson());
  _roundTrips('flow-view.json', (j) => FlowView.fromJson(j).toJson());
  _roundTrips('approval.json', (j) => Approval.fromJson(j).toJson());
  _roundTrips(
    'journaled-signal.json',
    (j) => JournaledSignalView.fromJson(j).toJson(),
  );

  test('unknown enum wire value throws', () {
    expect(() => PresenceState.fromWire('Nope'), throwsFormatException);
  });
}
