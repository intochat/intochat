import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  group('children', childTests);
  testWidgets('button with command posts act with resolved address', (
    tester,
  ) async {
    final sent = <Map<String, dynamic>>[];
    final ctx = NodeContext(
      view: 'local/apps/settings/view',
      generation: 3,
      send: (event) async {
        sent.add(event.toJson());
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

  Future<List<Map<String, dynamic>>> pressRoot(
    WidgetTester tester,
    String view, {
    String? acts,
  }) async {
    final sent = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: ViewHost(
            knowsApp: (_) => false,
            kind: 'view',
            name: view,
            watch: (_, _) => const Stream.empty(),
            onAction: (event) async {
              sent.add(event.toJson());
              return {'delivered': true};
            },
            load: (kind, name) async => PublishedViewPayload.fromJson({
              'generation': 4,
              'view': {
                'title': 'Root',
                'acts': ?acts,
                'root': {
                  'kind': 'button',
                  'key': 'append',
                  'label': 'Append point',
                  'command': {
                    'target': '.',
                    'signal': 'gallery.append',
                    'args': <String, Object?>{},
                  },
                },
              },
            }),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('Append point'));
    await tester.pumpAndSettle();
    return sent;
  }

  testWidgets('a root button of a view the server marks for /act sends /act', (
    tester,
  ) async {
    final sent = await pressRoot(
      tester,
      'local/apps/gallery/view',
      acts: 'act',
    );

    expect(sent, [
      {
        'kind': 'act',
        'neuron': 'local/apps/gallery',
        'target': '.',
        'signal': 'gallery.append',
        'args': <String, Object?>{},
      },
    ]);
  });

  // There is no key activation: every view's button sends its command to
  // POST /act, for the neuron that drew the view, whatever kind it is.
  for (final (view, neuron) in [
    ('local/apps/creator/view', 'local/apps/creator'),
    ('local/apps/sandboxed/view', 'local/apps/sandboxed'),
    ('local/units/bank', 'local/units/bank'),
    ('local/bank', 'local/bank'),
  ]) {
    testWidgets('a root button of $view sends its command over /act', (
      tester,
    ) async {
      final sent = await pressRoot(tester, view);

      expect(sent, [
        {
          'kind': 'act',
          'neuron': neuron,
          'target': '.',
          'signal': 'gallery.append',
          'args': <String, Object?>{},
        },
      ]);
    });
  }

  testWidgets('a button with an intent performs it and sends nothing', (
    tester,
  ) async {
    final sent = <Map<String, dynamic>>[];
    final performed = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: ViewHost(
            knowsApp: (app) => app == 'trace',
            kind: 'view',
            name: 'local/units/bank',
            watch: (_, _) => const Stream.empty(),
            onAction: (event) async {
              sent.add(event.toJson());
              return {'delivered': true};
            },
            onActivate: performed.add,
            load: (kind, name) async => PublishedViewPayload.fromJson({
              'generation': 4,
              'view': {
                'title': 'Root',
                'root': {
                  'kind': 'button',
                  'key': 'open',
                  'label': 'Open trace',
                  'intent': {
                    'intentKind': 'OpenWindow',
                    'arg':
                        'os://window/open?app=trace&input=t-1&transition=fade',
                  },
                },
              },
            }),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('Open trace'));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
    expect(performed, [
      {
        'kind': 'os_uri',
        'uri': 'os://window/open?app=trace&input=t-1',
        'host': 'window',
        'intent': 'os://window/open?app=trace&input=t-1&transition=fade',
      },
    ]);
  });

  testWidgets('a root field submits its text over /act', (tester) async {
    final sent = <Map<String, dynamic>>[];
    final ctx = NodeContext(
      view: 'local/apps/notes/view',
      generation: 2,
      send: (event) async {
        sent.add(event.toJson());
        return {'delivered': true};
      },
    );
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: Builder(
            builder: (_) => NodeRenderer.render(
              const FieldNode(
                key: 'note',
                name: 'apps/notes/note',
                fieldKind: FieldKind.text,
                placeholder: null,
                onSubmit: Command(
                  target: '.',
                  signal: 'notes.save',
                  args: {'draft': 'd1'},
                  props: null,
                ),
                automationName: 'Note',
              ),
              ctx,
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField), 'hello');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();

    expect(sent.where((event) => event['kind'] == 'view'), isEmpty);
    expect(sent.last, {
      'kind': 'act',
      'neuron': 'local/apps/notes',
      'target': '.',
      'signal': 'notes.save',
      'args': {'draft': 'd1'},
      'value': 'hello',
    });
  });

  test('act on a non-interactive context sends nothing', () async {
    const ctx = NodeContext(view: 'local/apps/settings/view');
    await ctx.act('.', 'toggle.set', const {});
  });

  testWidgets('field and navItem dispatch Command and expose automationName', (
    tester,
  ) async {
    final sent = <Map<String, dynamic>>[];
    final ctx = NodeContext(
      view: 'local/apps/gallery/view',
      generation: 2,
      send: (event) async {
        sent.add(event.toJson());
        return {'delivered': true};
      },
    );

    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: Builder(
            builder: (_) => NodeRenderer.render(
              const ColumnNode(
                gap: 8,
                padding: 0,
                extents: null,
                children: [
                  FieldNode(
                    key: 'search',
                    name: 'apps/gallery/search',
                    fieldKind: FieldKind.search,
                    placeholder: 'Filter controls',
                    onSubmit: Command(
                      target: 'filter-slot',
                      signal: 'control.submit',
                      args: {'scope': 'all'},
                      props: null,
                    ),
                    automationName: 'Search controls',
                  ),
                  NavItemNode(
                    key: 'nav-overview',
                    label: 'Overview',
                    icon: 'house',
                    action: 'overview',
                    selected: false,
                    badge: null,
                    command: Command(
                      target: '.',
                      signal: 'gallery.navigate',
                      args: {'page': 'overview'},
                      props: null,
                    ),
                    automationName: 'Open overview',
                  ),
                ],
              ),
              ctx,
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.bySemanticsLabel('Search controls'), findsWidgets);
    expect(find.bySemanticsLabel('Open overview'), findsWidgets);

    await tester.tap(find.byKey(const ValueKey('nav:nav-overview')));
    await tester.pumpAndSettle();
    expect(sent.last, {
      'kind': 'act',
      'neuron': 'local/apps/gallery',
      'target': '.',
      'signal': 'gallery.navigate',
      'args': {'page': 'overview'},
    });

    await tester.enterText(find.byType(TextField), 'button');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();
    expect(sent.last, {
      'kind': 'act',
      'neuron': 'local/apps/gallery',
      'target': 'filter-slot',
      'signal': 'control.submit',
      'args': {'scope': 'all'},
      'value': 'button',
    });
  });

  testWidgets('a slot is read by its app key path and redraws on its own', (
    tester,
  ) async {
    const parent = 'local/apps/settings/view';
    const child = 'local/apps/settings/children/dark-mode';
    final kinds = <String, String>{};
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
              kinds[name] = kind;
              return PublishedViewPayload.fromJson(switch (name) {
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
                          'refKind': 'child',
                          'neuronUri': 'dark-mode',
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
              });
            },
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Dark: off'), findsOneWidget);
    final parentLoads = loads[parent];
    final childLoads = loads[child]!;

    expect(kinds[child], 'child');

    // A slot child has no watchable name: it reloads on its refresh timer.
    on = true;
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    expect(find.text('Dark: on'), findsOneWidget);
    expect(loads[child], childLoads + 1);
    expect(loads[parent], parentLoads, reason: 'the parent does not redraw');
  });
}

const _root = 'local/apps/settings/view';
const _slot = 'local/apps/settings/children/dark-mode';
const _nested = 'local/apps/settings/children/dark-mode/accent';

Map<String, dynamic> _view(String title, List<Map<String, dynamic>> children) =>
    {
      'generation': 1,
      'view': {
        'title': title,
        'root': {'kind': 'column', 'children': children},
      },
    };

Widget _host(
  Future<Object?> Function(Map<String, dynamic>) onAction,
) => MaterialApp(
  theme: UiTheme.intochat(Brightness.light),
  home: Scaffold(
    body: ViewHost(
      knowsApp: (_) => false,
      kind: 'view',
      name: _root,
      watch: (_, _) => const Stream.empty(),
      onAction: (action) => onAction(action.toJson()),
      load: (kind, name) async => PublishedViewPayload.fromJson(switch (name) {
        _root => _view('Settings', [
          {'kind': 'neuronRef', 'refKind': 'child', 'neuronUri': 'dark-mode'},
        ]),
        _slot => _view('Dark', [
          {'kind': 'text', 'value': 'Dark'},
          {'kind': 'neuronRef', 'refKind': 'child', 'neuronUri': 'accent'},
        ]),
        _nested => _view('Accent', [
          {'kind': 'text', 'value': 'Accent'},
        ]),
        _ => throw StateError('Unknown $name'),
      }),
    ),
  ),
);

void childTests() {
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
