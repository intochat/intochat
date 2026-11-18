import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  group('slots', slotTests);
  testWidgets('button with command posts act with resolved address', (
    tester,
  ) async {
    final sent = <Map<String, dynamic>>[];
    final ctx = NodeContext(
      view: 'local/apps/settings/view',
      generation: 3,
      send: (event) async {
        sent.add(event);
        return {'delivered': true};
      },
    );
    // Harness: the Button model gains `command` later; a button that carries one calls act.
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: TextButton(
            key: const Key('dark'),
            onPressed: () => ctx.act('dark-mode', 'toggle.set', {'on': true}),
            child: const Text('Dark'),
          ),
        ),
      ),
    );
    await tester.tap(find.byKey(const Key('dark')));
    await tester.pumpAndSettle();

    expect(sent, [
      {
        'kind': 'act',
        'neuron': 'local/apps/settings',
        'target': 'dark-mode',
        'signal': 'toggle.set',
        'args': {'on': true},
      },
    ]);
  });

  test('act on a non-interactive context sends nothing', () async {
    const ctx = NodeContext(view: 'local/apps/settings/view');
    await ctx.act('.', 'toggle.set', const {});
  });

  testWidgets('slot redraws only on its own StateChanged', (tester) async {
    const parent = 'local/apps/settings/view';
    const child = 'local/app.toggle/local/app.settings/default/dark-mode';
    final streams = {
      parent: StreamController<void>.broadcast(),
      child: StreamController<void>.broadcast(),
    };
    for (final s in streams.values) {
      addTearDown(s.close);
    }
    final loads = <String, int>{};
    var on = false;

    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: ViewHost(
            knowsApp: (_) => false,
            kind: 'view',
            name: parent,
            watch: (_, name) => streams[name]?.stream ?? const Stream.empty(),
            onAction: (_) async => null,
            load: (kind, name) async {
              loads[name] = (loads[name] ?? 0) + 1;
              return switch (name) {
                parent => {
                  'generation': 1,
                  'view': {
                    'title': 'Settings',
                    'root': {
                      'kind': 'column',
                      'children': [
                        {'kind': 'text', 'value': 'Settings header'},
                        {
                          'kind': 'neuronRef',
                          'refKind': 'slot',
                          'neuronUri': child,
                        },
                      ],
                    },
                  },
                },
                child => {
                  'generation': on ? 2 : 1,
                  'view': {
                    'title': 'Dark mode',
                    'root': {
                      'kind': 'text',
                      'value': on ? 'Dark: on' : 'Dark: off',
                    },
                  },
                },
                _ => throw StateError('Unknown $name'),
              };
            },
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Dark: off'), findsOneWidget);
    final parentLoads = loads[parent];
    final childLoads = loads[child]!;

    on = true;
    streams[child]!.add(null);
    await tester.pumpAndSettle();

    expect(find.text('Dark: on'), findsOneWidget);
    expect(loads[child], childLoads + 1);
    expect(loads[parent], parentLoads, reason: 'the parent does not redraw');
  });
}

const _root = 'local/apps/settings/view';
const _slot = 'local/app.toggle/local/app.settings/default/dark-mode';
const _nested = 'local/app.accent/$_slot/accent';

Map<String, dynamic> _view(String title, List<Map<String, dynamic>> children) =>
    {
      'generation': 1,
      'view': {
        'title': title,
        'root': {'kind': 'column', 'children': children},
      },
    };

Widget _host(Future<Object?> Function(Map<String, dynamic>) onAction) =>
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: ViewHost(
          knowsApp: (_) => false,
          kind: 'view',
          name: _root,
          watch: (_, _) => const Stream.empty(),
          onAction: onAction,
          load: (kind, name) async => switch (name) {
            _root => _view('Settings', [
              {'kind': 'neuronRef', 'refKind': 'slot', 'neuronUri': _slot},
            ]),
            _slot => _view('Dark', [
              {'kind': 'text', 'value': 'Dark'},
              {'kind': 'neuronRef', 'refKind': 'slot', 'neuronUri': _nested},
            ]),
            _nested => _view('Accent', [
              {'kind': 'text', 'value': 'Accent'},
            ]),
            _ => throw StateError('Unknown $name'),
          },
        ),
      ),
    );

void slotTests() {
  late Map<String, NodeContext> contexts;
  setUp(() {
    contexts = {};
    ViewHost.debugOnContext = (name, ctx) => contexts[name] = ctx;
  });
  tearDown(() => ViewHost.debugOnContext = null);

  testWidgets('an act inside a slot names the root and the slot key path', (
    tester,
  ) async {
    final sent = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      _host((event) async {
        sent.add(event);
        return {'delivered': true};
      }),
    );
    await tester.pumpAndSettle();

    await contexts[_slot]!.act('.', 'toggle.set', {'on': true});
    await contexts[_nested]!.act('.', 'accent.set', const {});
    await contexts[_slot]!.act('./accent', 'accent.set', const {});
    await tester.pumpAndSettle();

    expect(sent.map((e) => (e['neuron'], e['target'])).toList(), [
      ('local/apps/settings', 'dark-mode'),
      ('local/apps/settings', 'dark-mode/accent'),
      ('local/apps/settings', 'dark-mode/accent'),
    ]);
  });

  testWidgets('a 409 from act shows the reason', (tester) async {
    await tester.pumpWidget(
      _host((event) async {
        if (event['kind'] == 'act') {
          throw UiRequestException(
            'POST',
            '/brains/local/act',
            409,
            '{"error":"ghost of local/apps/settings refused the command.","reason":"UnknownChild"}',
          );
        }
        return null;
      }),
    );
    await tester.pumpAndSettle();

    final outcome = await contexts[_slot]!.act('ghost', 'toggle.set', const {});
    await tester.pump();

    expect(outcome, ActivationOutcome.refused);
    expect(find.textContaining('UnknownChild'), findsOneWidget);
  });
}
