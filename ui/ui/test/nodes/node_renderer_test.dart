import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'node_samples.dart';

const _view = 'local/apps/sample/view';

Map<String, dynamic> _published(
  Map<String, dynamic> root, {
  int generation = 3,
}) => {
  'generation': generation,
  'view': {'title': 'Sample', 'root': root},
};

Future<void> _host(
  WidgetTester tester,
  Map<String, dynamic> root, {
  List<Map<String, dynamic>>? events,
  Map<String, Map<String, dynamic>> fields = const {},
  Object? Function(Map<String, dynamic> event)? answer,
  String kind = 'view',
  String name = _view,
  ValueChanged<Map<String, dynamic>>? onActivate,
  bool Function(String app)? knowsApp,
  Stream<void> Function(String kind, String name)? watch,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.light('intochat'),
      home: Scaffold(
        body: ViewHost(
          kind: kind,
          name: name,
          load: (kind, name) async => kind == 'field'
              ? fields[name] ?? {'value': ''}
              : _published(root),
          watch: watch ?? (kind, name) => const Stream.empty(),
          onAction: (event) async {
            events?.add(event);
            return answer?.call(event);
          },
          onActivate: onActivate,
          knowsApp: knowsApp,
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

/// The kinds `Node.fromJson` dispatches, read from the generated models.
List<String> _generatedKinds() {
  final source = File('../core/lib/src/generated/node_models.dart')
      .readAsStringSync();
  return [
    for (final match in RegExp(
      r"'(\w+)' => \w+\.fromJson\(json\)",
    ).allMatches(source))
      match.group(1)!,
  ];
}

void main() {
  test('there is a sample for every generated kind', () {
    final kinds = _generatedKinds();
    expect(kinds, hasLength(36));
    expect(nodeSamples.keys.toSet(), kinds.toSet());
    for (final MapEntry(:key, :value) in nodeSamples.entries) {
      expect(Node.fromJson(value).kind, key);
      expect(Node.fromJson(value), isNot(isA<UnknownNode>()), reason: key);
    }
  });

  for (final MapEntry(key: kind, value: sample) in nodeSamples.entries) {
    testWidgets('every generated kind renders without throwing: $kind', (
      tester,
    ) async {
      tester.view.physicalSize = const Size(1280, 900);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      await _host(tester, sample);
      expect(tester.takeException(), isNull);
      expect(find.text('This component cannot be displayed.'), findsNothing);
      await tester.pumpWidget(const SizedBox());
      await tester.pump(const Duration(seconds: 5));
    });
  }

  testWidgets('an unknown kind renders the declared fallback', (tester) async {
    await _host(tester, {'kind': 'hologram'});
    expect(find.text('This component cannot be displayed.'), findsOneWidget);
  });

  testWidgets('button emits {kind:view,key} with the generation it saw', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, nodeSamples['button']!, events: events);
    await tester.tap(find.widgetWithText(UiKitButton, 'Go'));
    await tester.pumpAndSettle();
    expect(events, [
      {'kind': 'view', 'name': _view, 'generation': 3, 'key': 'go'},
    ]);
  });

  testWidgets('a tab and a select option activate by key alone', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, nodeSamples['tabs']!, events: events);
    await tester.tap(find.text('A'));
    await tester.pumpAndSettle();
    expect(events.single['key'], 'tab-a');
    expect(events.single['kind'], 'view');
  });

  testWidgets('field reads, then posts each edit to the field neuron', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    await _host(
      tester,
      nodeSamples['field']!,
      events: events,
      fields: {
        'local/apps/sample/title': {'value': 'Draft', 'version': 1},
      },
    );
    expect(find.text('Draft'), findsOneWidget);
    await tester.enterText(find.byType(TextField), 'Final');
    await tester.pumpAndSettle();
    expect(events.last, {
      'kind': 'field',
      'name': 'local/apps/sample/title',
      'value': 'Final',
    });
  });

  testWidgets('field re-reads on FieldChanged', (tester) async {
    final changes = StreamController<void>.broadcast();
    addTearDown(changes.close);
    final values = {
      'local/apps/sample/title': <String, dynamic>{'value': 'One'},
    };
    final watched = <String>[];
    await _host(
      tester,
      nodeSamples['field']!,
      fields: values,
      watch: (kind, name) {
        watched.add('$kind:$name');
        return kind == 'field' ? changes.stream : const Stream.empty();
      },
    );
    expect(watched, contains('field:local/apps/sample/title'));
    values['local/apps/sample/title'] = {'value': 'Two'};
    changes.add(null);
    await tester.pumpAndSettle();
    expect(find.text('Two'), findsOneWidget);
  });

  testWidgets('a secret field never shows the stored value', (tester) async {
    await _host(
      tester,
      {...nodeSamples['field']!, 'fieldKind': 'Secret'},
      fields: {
        'local/apps/sample/title': {'value': '••••••••'},
      },
    );
    expect(find.text('••••••••'), findsNothing);
  });

  testWidgets('a field with onSubmit submits through the view', (tester) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, {
      ...nodeSamples['field']!,
      'onSubmit': {'owner': 'apps/sample', 'signal': 'search'},
    }, events: events);
    await tester.enterText(find.byType(TextField), 'query');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();
    expect(events.last, {
      'kind': 'view',
      'name': _view,
      'generation': 3,
      'key': 'title',
      'value': 'query',
    });
  });

  testWidgets('a returned OpenWindow intent opens an app the shell knows', (
    tester,
  ) async {
    final opened = <Map<String, dynamic>>[];
    await _host(
      tester,
      nodeSamples['button']!,
      answer: (_) => {
        'intent': {
          'intentKind': 'OpenWindow',
          'arg': 'os://window/open?app=trace&input=run-1',
        },
      },
      onActivate: opened.add,
      knowsApp: (app) => app == 'trace',
    );
    await tester.tap(find.widgetWithText(UiKitButton, 'Go'));
    await tester.pumpAndSettle();
    expect(opened.single['kind'], 'os_uri');
    expect(opened.single['uri'], 'os://window/open?app=trace&input=run-1');
  });

  group('intent gate', () {
    IntentNode open(String arg) =>
        IntentNode(intentKind: IntentKind.openWindow, arg: arg);

    test('refuses an external URL', () {
      expect(IntentGate.check(open('https://evil.example/')), isNull);
      expect(IntentGate.check(open('javascript:alert(1)')), isNull);
      expect(IntentGate.check(open('os://evil.example/open?app=x')), isNull);
    });

    test('refuses an app the shell does not know', () {
      expect(
        IntentGate.check(
          open('os://window/open?app=nope'),
          knowsApp: (app) => app == 'trace',
        ),
        isNull,
      );
      expect(IntentGate.check(open('os://window/open?app=../x')), isNull);
    });

    test('allows a known app window, copy and settings', () {
      expect(
        IntentGate.check(
          open('os://window/open?app=trace'),
          knowsApp: (app) => app == 'trace',
        )?['uri'],
        'os://window/open?app=trace',
      );
      expect(
        IntentGate.check(
          const IntentNode(intentKind: IntentKind.copy, arg: 'x'),
        ),
        isNotNull,
      );
      expect(
        IntentGate.check(
          const IntentNode(intentKind: IntentKind.openSettings, arg: 'keys'),
        )?['uri'],
        'os://window/open?app=settings&section=keys',
      );
    });
  });

  testWidgets('a refused intent is not performed', (tester) async {
    final opened = <Map<String, dynamic>>[];
    await _host(
      tester,
      nodeSamples['button']!,
      answer: (_) => {
        'intent': {'intentKind': 'OpenWindow', 'arg': 'https://evil.example'},
      },
      onActivate: opened.add,
    );
    await tester.tap(find.widgetWithText(UiKitButton, 'Go'));
    await tester.pumpAndSettle();
    expect(opened, isEmpty);
  });

  testWidgets('a copy intent copies its text', (tester) async {
    String? copied;
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      (call) async {
        if (call.method == 'Clipboard.setData') {
          copied = (call.arguments as Map)['text'] as String?;
        }
        return null;
      },
    );
    addTearDown(
      () => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
        SystemChannels.platform,
        null,
      ),
    );
    await _host(
      tester,
      nodeSamples['button']!,
      answer: (_) => {
        'intent': {'kind': 'Copy', 'arg': 'hello'},
      },
    );
    await tester.tap(find.widgetWithText(UiKitButton, 'Go'));
    await tester.pump();
    expect(copied, 'hello');
    await tester.pump(const Duration(seconds: 5));
    await tester.pumpAndSettle();
  });

  testWidgets('approval card renders no buttons in an app view', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, nodeSamples['approvalCard']!, events: events);
    expect(find.text('Approve change'), findsOneWidget);
    expect(find.text('Approve'), findsNothing);
    expect(find.text('Reject'), findsNothing);
    expect(find.byType(UiKitButton), findsNothing);
  });

  testWidgets('approval card resolves on the confirmation surface', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.light('intochat'),
        home: Scaffold(
          body: ViewHost(
            kind: 'confirmation',
            name: 'local/confirmations',
            load: (kind, name) async => {
              'title': 'Confirmations',
              'root': nodeSamples['approvalCard'],
            },
            watch: (kind, name) => const Stream.empty(),
            onAction: (event) async => events.add(event),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('Approve'));
    await tester.pumpAndSettle();
    expect(events.single, {
      'kind': 'confirmation',
      'name': 'local/confirmations',
      'action': 'Resolve:plan-1:true',
    });
  });

  testWidgets('a view that has not published shows it is preparing', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ViewHost(
            kind: 'view',
            name: _view,
            load: (kind, name) async => {'generation': 0},
            watch: (kind, name) => const Stream.empty(),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Preparing this view'), findsOneWidget);
  });

  testWidgets('a failed read offers retry', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ViewHost(
            kind: 'view',
            name: _view,
            load: (kind, name) async => throw StateError('down'),
            watch: (kind, name) => const Stream.empty(),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Retry'), findsOneWidget);
  });
}
