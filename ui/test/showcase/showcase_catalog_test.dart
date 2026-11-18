import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _unsupported = 'This component cannot be displayed.';

List<Map<String, dynamic>> _catalog() =>
    (jsonDecode(
              File('lib/src/generated/showcase_catalog.json').readAsStringSync(),
            )
            as List)
        .cast<Map<String, dynamic>>();

bool _isEmit(Object? v) =>
    v is Map && v['owner'] is String && v['signal'] is String;

/// Every `{key, ... emit}` node inside an example: the controls whose events the test triggers.
void _emitters(Object? json, List<Map<String, dynamic>> into) {
  if (json is Map) {
    final map = json.cast<String, dynamic>();
    if (map['key'] is String && map.values.any(_isEmit)) into.add(map);
    for (final v in map.values) {
      _emitters(v, into);
    }
  } else if (json is List) {
    for (final v in json) {
      _emitters(v, into);
    }
  }
}

Map<String, dynamic> _emitOf(Map<String, dynamic> node) =>
    (node.values.firstWhere(_isEmit) as Map).cast<String, dynamic>();

/// The declared names of an emit's args shape: a map's keys, or a list of `{name}` / strings.
Set<String> _argNames(Object? args) => switch (args) {
  Map() => args.keys.map((k) => '$k').toSet(),
  List() => {
    for (final a in args)
      if (a is Map && a['name'] != null) '${a['name']}' else if (a is String) a,
  },
  _ => {},
};

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> example,
) async {
  final sent = <Map<String, dynamic>>[];
  final ctx = NodeContext(
    view: 'local/apps/showcase/view',
    generation: 1,
    send: (event) async {
      sent.add(event);
      return null;
    },
  );
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.light('intochat'),
      home: Scaffold(
        body: SingleChildScrollView(
          child: Builder(
            builder: (_) => NodeRenderer.render(Node.fromJson(example), ctx),
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return sent;
}

/// Taps, toggles or submits the control found by [finder].
Future<void> _trigger(WidgetTester tester, Finder finder) async {
  final field = find.descendant(
    of: finder,
    matching: find.byType(EditableText),
  );
  if (field.evaluate().isNotEmpty) {
    await tester.enterText(field.first, 'hello');
    await tester.testTextInput.receiveAction(TextInputAction.done);
  } else {
    await tester.tap(finder.first);
  }
  await tester.pumpAndSettle();
}

Future<void> _check(WidgetTester tester, Map<String, dynamic> example) async {
  final sent = await _pump(tester, example);
  expect(find.text(_unsupported), findsNothing);

  final emitters = <Map<String, dynamic>>[];
  _emitters(example, emitters);
  for (final node in emitters) {
    final key = node['key'] as String;
    expect(find.byKey(Key(key)), findsWidgets, reason: 'key $key');
    final name = node['automationName'];
    if (name is String && name.isNotEmpty) {
      expect(find.bySemanticsLabel(name), findsWidgets);
    }
    final emit = _emitOf(node);
    sent.clear();
    await _trigger(tester, find.byKey(Key(key)));
    final hit = sent.where((e) => e['key'] == key).toList();
    expect(hit, isNotEmpty, reason: 'no activation recorded for $key');
    expect(emit['signal'], isNotEmpty);
    final declared = _argNames(emit['args']);
    if (declared.isNotEmpty) {
      final event = hit.last['event'];
      expect(event, isA<Map>(), reason: 'no typed event for $key');
      expect(
        (event as Map).keys.map((k) => '$k').toSet().containsAll(declared),
        isTrue,
        reason: 'event $event lacks $declared',
      );
    }
  }
}

void main() {
  final catalog = _catalog();

  group('showcase examples', () {
    for (final entry in catalog) {
      final examples = (entry['examples'] as List).cast<Map<String, dynamic>>();
      for (var i = 0; i < examples.length; i++) {
        testWidgets('${entry['kind']} example $i renders and emits', (
          tester,
        ) async {
          await _check(tester, examples[i]);
        });
      }
    }
  });

  group('harness', () {
    testWidgets('a keyed button example is found and emits its typed event', (
      tester,
    ) async {
      await _check(tester, {
        'kind': 'button',
        'key': 'save',
        'label': 'Save',
        'emit': {'owner': 'local/apps/x', 'signal': 'Saved', 'args': {}},
      });
    });

    testWidgets('an unknown kind is reported as unsupported', (tester) async {
      await _pump(tester, {'kind': 'doesNotExist'});
      expect(find.text(_unsupported), findsOneWidget);
    });

    test('the catalog is sorted by kind', () {
      final kinds = [for (final e in catalog) e['kind'] as String];
      expect(kinds, [...kinds]..sort());
    });
  });
}
