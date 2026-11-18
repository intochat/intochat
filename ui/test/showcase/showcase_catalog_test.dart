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

final _models = File('lib/src/generated/node_models.dart').readAsStringSync();

/// kind -> field names, for every generated `Node` model (read from the source of truth).
Map<String, Set<String>> _modelFields() {
  final result = <String, Set<String>>{};
  final classes = RegExp(
    r'final class \w+ extends Node \{(.*?)\n\}\n',
    dotAll: true,
  );
  for (final m in classes.allMatches(_models)) {
    final body = m.group(1)!;
    final kind = RegExp(r"get kind => '(\w+)'").firstMatch(body)?.group(1);
    if (kind == null) continue;
    result[kind] = {
      for (final f in RegExp(
        r'^  final [\w<>?, ]+ (\w+);',
        multiLine: true,
      ).allMatches(body))
        f.group(1)!,
    };
  }
  return result;
}

final _fields = _modelFields();
final _keyedKinds = {
  for (final e in _fields.entries)
    if (e.value.contains('key')) e.key,
};

/// Keyed kinds whose generated model has no `automationName` yet. It may only shrink:
/// the test fails when a listed kind gains the field, so the entry must then be removed.
const knownWithoutAutomationName = <String>{
  'appShell',
  'approvalCard',
  'button',
  'conversation',
  'field',
  'navItem',
  'statCard',
  'switch',
  'webBrowser',
};

/// Every keyed node inside an example (a map whose kind declares a `key` field).
void _keyed(Object? json, List<Map<String, dynamic>> into) {
  if (json is Map) {
    final map = json.cast<String, dynamic>();
    if (_keyedKinds.contains(map['kind']) && map['key'] is String) {
      into.add(map);
    }
    for (final v in map.values) {
      _keyed(v, into);
    }
  } else if (json is List) {
    for (final v in json) {
      _keyed(v, into);
    }
  }
}

Map<String, dynamic>? _emitOf(Map<String, dynamic> node) {
  for (final v in node.values) {
    if (_isEmit(v)) return (v as Map).cast<String, dynamic>();
  }
  return null;
}

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

  final keyed = <Map<String, dynamic>>[];
  _keyed(example, keyed);
  for (final node in keyed) {
    final key = node['key'] as String;
    final kind = node['kind'] as String;
    expect(find.byKey(Key(key)), findsOneWidget, reason: 'key $key');

    final name = node['automationName'];
    if (name is String && name.isNotEmpty) {
      expect(find.bySemanticsLabel(name), findsWidgets);
    } else {
      expect(
        knownWithoutAutomationName,
        contains(kind),
        reason: '$kind example "$key" declares no automationName',
      );
    }

    final emit = _emitOf(node);
    if (emit == null) continue;
    sent.clear();
    await _trigger(tester, find.byKey(Key(key)));
    final hit = sent.where((e) => e['key'] == key).toList();
    expect(hit, isNotEmpty, reason: 'no activation recorded for $key');
    final declared = _argNames(emit['args']);
    if (declared.isNotEmpty) {
      final event = hit.last['event'];
      expect(event, isA<Map>(), reason: 'no typed event for $key');
      expect(
        (event as Map).keys.map((k) => '$k').toSet(),
        declared,
        reason: 'event $event does not match declared args $declared',
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
    testWidgets('a keyed button example is found and activated by its key', (
      tester,
    ) async {
      await _check(tester, {
        'kind': 'button',
        'key': 'save',
        'label': 'Save',
        'emit': {'owner': 'local/apps/x', 'signal': 'Saved', 'args': {}},
      });
    });

    testWidgets('a keyed control without an emit is still found by its key', (
      tester,
    ) async {
      await _check(tester, {'kind': 'switch', 'key': 'plain', 'label': 'x'});
    });

    testWidgets('emitEvent sends the control key and the exact typed event', (
      tester,
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
      const emit = Emit(owner: 'local/apps/x', signal: 'Submitted', args: null);
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: TextButton(
              key: const Key('submit'),
              onPressed: () => ctx.emitEvent('submit', emit, {'text': 'hello'}),
              child: const Text('go'),
            ),
          ),
        ),
      );
      await tester.tap(find.byKey(const Key('submit')));
      await tester.pumpAndSettle();
      expect(sent, hasLength(1));
      expect(sent.single['key'], 'submit');
      expect(sent.single['event'], {'text': 'hello'});
    });

    testWidgets('an unknown kind is reported as unsupported', (tester) async {
      await _pump(tester, {'kind': 'doesNotExist'});
      expect(find.text(_unsupported), findsOneWidget);
    });

    test('every generated model with a key field is keyed by the renderer', () {
      expect(_keyedKinds, isNotEmpty);
      for (final kind in _keyedKinds) {
        final node = Node.fromJson({'kind': kind, 'key': 'probe'});
        expect(NodeRenderer.keyOf(node), 'probe', reason: kind);
      }
    });

    test('knownWithoutAutomationName only shrinks', () {
      for (final kind in knownWithoutAutomationName) {
        expect(_keyedKinds, contains(kind));
        expect(
          _fields[kind],
          isNot(contains('automationName')),
          reason: '$kind now has automationName: remove it from the set',
        );
      }
      for (final kind in _keyedKinds) {
        if (!(_fields[kind]!.contains('automationName'))) {
          expect(knownWithoutAutomationName, contains(kind));
        }
      }
    });

    test('the catalog is sorted by kind', () {
      final kinds = [for (final e in catalog) e['kind'] as String];
      expect(kinds, [...kinds]..sort());
    });
  });
}
