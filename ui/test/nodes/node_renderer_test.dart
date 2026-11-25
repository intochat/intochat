import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

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
          knowsApp: knowsApp ?? (_) => false,
          kind: kind,
          name: name,
          load: (kind, name) async => _published(root),
          watch: watch ?? (kind, name) => const Stream.empty(),
          onAction: (event) async {
            events?.add(event);
            return answer?.call(event);
          },
          onActivate: onActivate,
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

/// The kinds `Node.fromJson` dispatches, read from the generated models.
List<String> _generatedKinds() {
  final path = File('lib/src/generated/node_models.dart').existsSync()
      ? 'lib/src/generated/node_models.dart'
      : '../core/lib/src/generated/node_models.dart';
  final source = File(path).readAsStringSync();
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
    expect(kinds, hasLength(55));
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

  testWidgets('typed text stays in the field until it submits', (tester) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, {
      ...nodeSamples['field']!,
      'onSubmit': {'target': '.', 'signal': 'save'},
    }, events: events);
    await tester.enterText(find.byType(TextField), 'Final');
    await tester.pump(const Duration(milliseconds: 300));
    await tester.pumpAndSettle();
    expect(events, isEmpty, reason: 'typing alone sends nothing');
    await tester.showKeyboard(find.byType(TextField));
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();
    expect(events.single, {
      'kind': 'view',
      'name': _view,
      'generation': 3,
      'key': 'title',
      'value': 'Final',
    });
  });

  testWidgets('a live field sends one command per pause in typing', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, {
      ...nodeSamples['field']!,
      'onSubmit': {
        'target': '.',
        'signal': 'draft',
        'args': {'uiLive': true},
      },
    }, events: events);
    await tester.enterText(find.byType(TextField), 'Fin');
    await tester.pump(const Duration(milliseconds: 100));
    await tester.enterText(find.byType(TextField), 'Final');
    await tester.pump(const Duration(milliseconds: 300));
    await tester.pumpAndSettle();
    expect(events.map((event) => event['value']), [
      'Final',
    ], reason: 'edits within the debounce send once');
    await tester.enterText(find.byType(TextField), 'Final draft');
    await tester.pump(const Duration(milliseconds: 300));
    await tester.pumpAndSettle();
    expect(events.map((event) => event['value']), ['Final', 'Final draft']);
  });

  testWidgets('a field shows the text the screen holds for it', (tester) async {
    await _host(tester, {
      ...nodeSamples['field']!,
      'onSubmit': {
        'target': '.',
        'signal': 'draft',
        'args': {'uiValue': 'Draft'},
      },
    });
    expect(find.text('Draft'), findsOneWidget);
  });

  testWidgets('a secret field never shows a value it is given', (tester) async {
    await _host(tester, {
      ...nodeSamples['field']!,
      'fieldKind': 'Secret',
      'onSubmit': {
        'target': '.',
        'signal': 'draft',
        'args': {'uiValue': 'hunter2'},
      },
    });
    expect(find.text('hunter2'), findsNothing);
  });

  testWidgets('a field with onSubmit submits through the view', (tester) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, {
      ...nodeSamples['field']!,
      'onSubmit': {'target': '.', 'signal': 'search'},
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

    test('refuses every window without a known-app set', () {
      expect(IntentGate.check(open('os://window/open?app=trace')), isNull);
    });

    test('rebuilds the window URI from the app and an opaque input only', () {
      final action = IntentGate.check(
        open(
          'os://window/open?app=trace&input=javascript:alert(1)&url=https://evil.example',
        ),
        knowsApp: (app) => app == 'trace',
      );
      final uri = Uri.parse(action!['uri'] as String);
      expect(uri.queryParameters, {
        'app': 'trace',
        'input': 'javascript:alert(1)',
      });
    });

    test('an unknown intent kind is refused, never a copy', () {
      expect(NodeContext.intentKindOf('Explode'), isNull);
      expect(NodeContext.intentKindOf('OpenWindow'), IntentKind.openWindow);
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

  testWidgets('a view host without a known-app set opens no window', (
    tester,
  ) async {
    final opened = <Map<String, dynamic>>[];
    await _host(
      tester,
      nodeSamples['button']!,
      answer: (_) => {
        'intent': {
          'intentKind': 'OpenWindow',
          'arg': 'os://window/open?app=tasks',
        },
      },
      onActivate: opened.add,
    );
    await tester.tap(find.widgetWithText(UiKitButton, 'Go'));
    await tester.pumpAndSettle();
    expect(opened, isEmpty);
  });

  testWidgets('an unknown intent kind performs nothing', (tester) async {
    final opened = <Map<String, dynamic>>[];
    var copied = false;
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      (call) async {
        if (call.method == 'Clipboard.setData') copied = true;
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
        'intent': {'intentKind': 'Explode', 'arg': 'x'},
      },
      onActivate: opened.add,
      knowsApp: (_) => true,
    );
    await tester.tap(find.widgetWithText(UiKitButton, 'Go'));
    await tester.pumpAndSettle();
    expect(opened, isEmpty);
    expect(copied, isFalse);
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

  testWidgets(
    'an approval card inside an app view renders read-only even with resolve metadata',
    (tester) async {
      final events = <Map<String, dynamic>>[];
      await _host(tester, nodeSamples['approvalCard']!, events: events);
      expect(find.text('Approve change'), findsOneWidget);
      expect(find.text('Pending approval'), findsOneWidget);
      expect(find.text('Approve'), findsNothing);
      expect(find.text('Reject'), findsNothing);
      expect(events, isEmpty);
    },
  );

  testWidgets('approval card resolves on the confirmation surface', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.light('intochat'),
        home: Scaffold(
          body: ViewHost(
            knowsApp: (_) => false,
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
      'planId': 'plan-1',
      'approve': true,
    });
  });

  testWidgets(
    'approval card buttons are active when ctx.interactive == true && resolve != null',
    (tester) async {
      final events = <Map<String, dynamic>>[];
      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light('intochat'),
          home: Scaffold(
            body: ViewHost(
              knowsApp: (_) => false,
              kind: 'confirmation',
              name: 'local/confirmations',
              enabled: true,
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
      expect(find.text('Approve'), findsOneWidget);
      expect(find.text('Reject'), findsOneWidget);
      await tester.tap(find.text('Approve'));
      await tester.pumpAndSettle();
      expect(events.single['approve'], isTrue);
    },
  );

  testWidgets(
    'approval card buttons are disabled/non-interactive when ctx.interactive == false',
    (tester) async {
      final events = <Map<String, dynamic>>[];
      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light('intochat'),
          home: Scaffold(
            body: ViewHost(
              knowsApp: (_) => false,
              kind: 'confirmation',
              name: 'local/confirmations',
              enabled: false,
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
      expect(find.text('Approve'), findsNothing);
      expect(find.text('Reject'), findsNothing);
      expect(events, isEmpty);
    },
  );

  testWidgets(
    'approval card buttons are disabled/non-interactive when resolve == null',
    (tester) async {
      final events = <Map<String, dynamic>>[];
      final cardWithoutResolve = {
        ...nodeSamples['approvalCard']!,
        'resolve': null,
      };
      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light('intochat'),
          home: Scaffold(
            body: ViewHost(
              knowsApp: (_) => false,
              kind: 'confirmation',
              name: 'local/confirmations',
              enabled: true,
              load: (kind, name) async => {
                'title': 'Confirmations',
                'root': cardWithoutResolve,
              },
              watch: (kind, name) => const Stream.empty(),
              onAction: (event) async => events.add(event),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Approve'), findsNothing);
      expect(find.text('Reject'), findsNothing);
      expect(events, isEmpty);
    },
  );

  // A unit view's neuronRef to the confirmation inbox is not hosted: only a viewport or the web browser is, and a host reached
  // through a ref never answers a confirmation, so the card stays display-only and nothing is sent.
  testWidgets('an embedded confirmation ref renders display-only', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    final loaded = <String>[];
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.light('intochat'),
        home: Scaffold(
          body: ViewHost(
            knowsApp: (_) => false,
            kind: 'view',
            name: 'local/x.watch',
            load: (kind, name) async {
              loaded.add(kind);
              return kind == 'confirmation'
                  ? {
                      'title': 'Confirmations',
                      'root': nodeSamples['approvalCard'],
                    }
                  : _published({
                      'kind': 'column',
                      'children': [
                        {
                          'kind': 'neuronRef',
                          'refKind': 'confirmation',
                          'neuronUri': 'local/confirmations',
                        },
                      ],
                    });
            },
            watch: (kind, name) => const Stream.empty(),
            onAction: (event) async => events.add(event),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(loaded, isNot(contains('confirmation')));
    expect(find.text('local/confirmations'), findsOneWidget);
    expect(find.text('Approve'), findsNothing);
    expect(events, isEmpty);
  });

  // Even a confirmation host reached as an embedded child (a viewport's root) renders its cards without answers.
  testWidgets('an embedded confirmation host never resolves', (tester) async {
    final events = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.light('intochat'),
        home: Scaffold(
          body: ViewHost(
            knowsApp: (_) => false,
            kind: 'viewport',
            name: 'local/apps/x/viewport',
            load: (kind, name) async => kind == 'viewport'
                ? {
                    'revision': 1,
                    'root': {
                      'kind': 'confirmation',
                      'name': 'local/confirmations',
                    },
                  }
                : {
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
    expect(find.text('Approve change'), findsOneWidget);
    expect(find.text('Approve'), findsNothing);
    expect(events, isEmpty);
  });

  testWidgets('a view that has not published shows it is preparing', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ViewHost(
            knowsApp: (_) => false,
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
            knowsApp: (_) => false,
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

  group('design system fixes', () {
    test('nodeToneOf and toneOf resolve ok, warn, info, accent', () {
      expect(nodeToneOf('ok'), UiNodeTone.done);
      expect(nodeToneOf('done'), UiNodeTone.done);
      expect(nodeToneOf('warn'), UiNodeTone.working);
      expect(nodeToneOf('working'), UiNodeTone.working);
      expect(nodeToneOf('accent'), UiNodeTone.accent);
      expect(nodeToneOf('info'), UiNodeTone.accent);
      expect(nodeToneOf('primary'), UiNodeTone.accent);
      expect(nodeToneOf('error'), UiNodeTone.error);
      expect(nodeToneOf('critical'), UiNodeTone.error);
      expect(toneOf('ok'), UiTone.done);
      expect(toneOf('warn'), UiTone.working);
      expect(toneOf('critical'), UiTone.error);
    });

    testWidgets(
      'SurfaceKind renders elevated, recessed, card, and acrylic distinctly',
      (tester) async {
        const c = UiColors.intochatLight;
        await _host(tester, {
          'kind': 'column',
          'children': [
            {
              'kind': 'surface',
              'surfaceKind': 'elevated',
              'children': [
                {'kind': 'text', 'value': 'Elevated-Content'},
              ],
            },
            {
              'kind': 'surface',
              'surfaceKind': 'recessed',
              'children': [
                {'kind': 'text', 'value': 'Recessed-Content'},
              ],
            },
            {
              'kind': 'surface',
              'surfaceKind': 'card',
              'children': [
                {'kind': 'text', 'value': 'Card-Content'},
              ],
            },
            {
              'kind': 'surface',
              'surfaceKind': 'acrylic',
              'children': [
                {'kind': 'text', 'value': 'Acrylic-Content'},
              ],
            },
          ],
        });
        final surfaces = tester
            .widgetList<UiMixSurface>(find.byType(UiMixSurface))
            .toList();
        expect(surfaces, hasLength(4));
        expect(surfaces[0].color, c.elevated);
        expect(surfaces[1].color, c.recessed);
        expect(surfaces[2].color, c.surface);
        expect(surfaces[3].color, c.chrome);
        expect(surfaces[3].blurSigma, greaterThan(0));
      },
    );

    testWidgets(
      'text with mono=true and neuronRef use Inter tabular figures, never raw monospace',
      (tester) async {
        await _host(tester, {
          'kind': 'column',
          'children': [
            {'kind': 'text', 'value': 'v1.0.0', 'mono': true},
            {
              'kind': 'neuronRef',
              'refKind': 'confirmation',
              'neuronUri': 'neuron://local/test',
            },
          ],
        });
        for (final st in tester.widgetList<SelectableText>(
          find.byType(SelectableText),
        )) {
          expect(st.style?.fontFamily, isNot('monospace'));
          expect(st.style?.fontFamily, contains('Inter'));
          expect(
            st.style?.fontFeatures,
            contains(const FontFeature.tabularFigures()),
          );
        }
      },
    );

    testWidgets('heading levels 1..5 use InterDisplay/Inter scale', (
      tester,
    ) async {
      await _host(tester, {
        'kind': 'column',
        'children': [
          for (var level = 1; level <= 5; level++)
            {'kind': 'heading', 'value': 'H$level', 'level': level},
        ],
      });
      expect(tester.widget<Text>(find.text('H1')).style?.fontSize, 26);
      expect(tester.widget<Text>(find.text('H2')).style?.fontSize, 20);
      expect(tester.widget<Text>(find.text('H3')).style?.fontSize, 16);
      expect(tester.widget<Text>(find.text('H4')).style?.fontSize, 14);
      expect(tester.widget<Text>(find.text('H5')).style?.fontSize, 13);
    });

    testWidgets('navItem renders 32px pill and dispatches key on tap', (
      tester,
    ) async {
      final events = <Map<String, dynamic>>[];
      await _host(tester, {
        'kind': 'navItem',
        'key': 'nav-settings',
        'label': 'Settings',
        'icon': 'settings',
        'action': 'settings',
        'selected': true,
        'badge': '2',
      }, events: events);
      final pill = find.byKey(const ValueKey('nav:nav-settings'));
      expect(pill, findsOneWidget);
      expect(tester.getSize(pill).height, 32);
      await tester.tap(pill);
      await tester.pumpAndSettle();
      expect(events.single['key'], 'nav-settings');
    });

    testWidgets(
      'approvalCard with resolve==null renders structured ApprovalCard instead of raw arrow text',
      (tester) async {
        await _host(tester, {...nodeSamples['approvalCard']!, 'resolve': null});
        expect(find.byType(ApprovalCard), findsOneWidget);
        expect(find.text('Stage: Open -> Won'), findsNothing);
        expect(find.text('Stage'), findsOneWidget);
        expect(find.text('Open'), findsOneWidget);
        expect(find.text('Won'), findsOneWidget);
      },
    );
  });
}
