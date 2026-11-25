import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _unsupported = 'This component cannot be displayed.';

List<Map<String, dynamic>> _catalog() => (jsonDecode(
  File('lib/src/generated/showcase_catalog.json').readAsStringSync(),
) as List).cast<Map<String, dynamic>>();

// A legacy emit ({owner, signal}) or a command ({target, signal}).
bool _isEmit(Object? v) =>
    v is Map &&
    (v['owner'] is String || v['target'] is String) &&
    v['signal'] is String;

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

/// Kinds a control neuron draws: their accessible name comes from the slot's
/// props (see `NodeContext.childProps`), not from a field of the node.
final _controlViewKinds = {
  for (final entry in _catalog())
    if (entry['isNeuron'] == true)
      for (final example in entry['examples'] as List)
        (example as Map)['kind'] as String,
};
final _keyedKinds = {
  for (final e in _fields.entries)
    if (e.value.contains('key')) e.key,
};

/// Keyed kinds whose generated model has no `automationName` yet. It may only shrink:
/// the test fails when a listed kind gains the field, so the entry must then be removed.
const knownWithoutAutomationName = <String>{};

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

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> example,
) async {
  final sent = <Map<String, dynamic>>[];
  final props = example['props'];
  final ctx = NodeContext(
    view: 'local/apps/showcase/view',
    generation: 1,
    // A control neuron's example carries its slot's props beside its node.
    childProps: props is Map ? props.cast<String, dynamic>() : const {},
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
  // An indeterminate example (a spinning ring) never settles; a few frames show it.
  await tester
      .pumpAndSettle(
        const Duration(milliseconds: 100),
        EnginePhase.sendSemanticsUpdate,
        const Duration(seconds: 2),
      )
      .catchError((Object _) => 0, test: (error) => error is FlutterError);
  return sent;
}

/// Taps, toggles or submits the control found by [finder].
Future<void> _trigger(WidgetTester tester, Finder finder) async {
  final field = find.descendant(
    of: finder,
    matching: find.byType(EditableText),
  );
  // A closable control (an info bar) is triggered by its dismiss control.
  final dismiss = find.descendant(
    of: finder,
    matching: find.byTooltip('Dismiss'),
  );
  if (field.evaluate().isNotEmpty) {
    await tester.enterText(field.first, 'hello');
    await tester.testTextInput.receiveAction(TextInputAction.done);
  } else if (dismiss.evaluate().isNotEmpty) {
    await tester.tap(dismiss.first);
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

    final props = node['props'];
    final name =
        node['automationName'] ??
        (props is Map ? props['automationName'] : null);
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
    // Every control sends its own command (a button's command, an info bar's
    // close, a field's onSubmit) through /act, with that command's signal.
    final signal = emit['signal'];
    final hit = sent
        .where((e) => e['kind'] == 'act' && e['signal'] == signal)
        .toList();
    expect(hit, isNotEmpty, reason: 'no activation recorded for $key');
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
        'automationName': 'Save',
        'command': {'target': '.', 'signal': 'Saved', 'args': {}},
      });
    });

    testWidgets('a keyed control without an emit is still found by its key', (
      tester,
    ) async {
      await _check(tester, {
        'kind': 'field',
        'key': 'plain',
        'name': 'apps/sample/plain',
        'fieldKind': 'Text',
        'automationName': 'x',
      });
    });

    testWidgets(
      'keyed container and card nodes expose their automationName semantics',
      (tester) async {
        await _check(tester, {
          'kind': 'appShell',
          'key': 'shell-main',
          'automationName': 'Main workspace shell',
          'body': {'kind': 'text', 'value': 'Ready'},
        });
        await _check(tester, {
          'kind': 'approvalCard',
          'key': 'approve-pr',
          'id': 'plan-1',
          'title': 'Publish Pull Request',
          'summary': 'Push verified branch',
          'rows': const <Map<String, dynamic>>[],
          'automationName': 'Publish Pull Request',
        });
        await _check(tester, {
          'kind': 'conversation',
          'key': 'conv-main',
          'automationName': 'Main conversation',
          'messages': const <Map<String, dynamic>>[],
          'composer': {
            'key': 'composer-main',
            'onSubmit': {'target': '.', 'signal': 'Send'},
          },
        });
        await _check(tester, {
          'kind': 'statCard',
          'key': 'stat-neurons',
          'label': 'Active Neurons',
          'value': '128',
          'automationName': 'Active Neurons',
        });
        await _check(tester, {
          'kind': 'webBrowser',
          'key': 'browser-preview',
          'url': 'https://intochat.io',
          'title': 'Studio Preview',
          'automationName': 'Studio Preview',
        });
      },
    );

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
        if (!(_fields[kind]!.contains('automationName')) &&
            !_controlViewKinds.contains(kind)) {
          expect(knownWithoutAutomationName, contains(kind));
        }
      }
    });

    test('every catalog entry has the view neuron shape', () {
      for (final entry in catalog) {
        expect(entry.keys.toSet(), {
          'kind',
          'isNeuron',
          'props',
          'state',
          'commands',
          'signals',
          'examples',
        }, reason: '${entry['kind']}');
        expect(entry['isNeuron'], isA<bool>());
        if (entry['isNeuron'] == false) {
          expect(_fields, contains(entry['kind']));
          expect(entry['commands'], isEmpty);
        }
      }
    });

    test('the catalog is sorted by kind', () {
      final kinds = [for (final e in catalog) e['kind'] as String];
      expect(kinds, [...kinds]..sort());
    });
  });
}
