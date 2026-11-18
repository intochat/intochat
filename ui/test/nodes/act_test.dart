import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
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
