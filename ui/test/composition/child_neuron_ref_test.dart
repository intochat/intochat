import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  Widget wrap(Widget child, {Brightness brightness = Brightness.light}) =>
      MaterialApp(
        theme: UiTheme.intochat(brightness),
        home: Scaffold(body: child),
      );

  testWidgets(
    'NeuronRef mounts live nested ViewHost for view, slot, app, unit, and empty refKind, but not browser or confirmation',
    (tester) async {
      final loaded = <String>[];

      await tester.pumpWidget(
        wrap(
          ViewHost(
            kind: 'view',
            name: 'local/apps/dashboard/view',
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              watch: (_, _) => const Stream.empty(),
              onAction: (_) async => null,
              load: (kind, name) async {
                loaded.add('$kind:$name');
                return PublishedViewPayload.fromJson(switch (name) {
                  'local/apps/dashboard/view' => {
                    'generation': 1,
                    'view': {
                      'title': 'Dashboard',
                      'root': {
                        'kind': 'column',
                        'children': [
                          {
                            'kind': 'neuronRef',
                            'refKind': 'view',
                            'neuronUri': 'local/views/summary',
                          },
                          {
                            'kind': 'neuronRef',
                            'refKind': 'child',
                            'neuronUri': 'orders.live',
                          },
                          {
                            'kind': 'neuronRef',
                            'refKind': 'app',
                            'neuronUri': 'billing',
                          },
                          {
                            'kind': 'neuronRef',
                            'refKind': 'unit',
                            'neuronUri': 'ledger',
                          },
                          {
                            'kind': 'neuronRef',
                            'refKind': '',
                            'neuronUri': 'neuron://local/metrics',
                          },
                          {
                            'kind': 'neuronRef',
                            'refKind': 'browser',
                            'neuronUri': 'local/browser/tab-1',
                          },
                          {
                            'kind': 'neuronRef',
                            'refKind': 'confirmation',
                            'neuronUri': 'local/confirmations',
                          },
                        ],
                      },
                    },
                  },
                  'local/views/summary' => {
                    'generation': 2,
                    'view': {
                      'title': 'Summary',
                      'root': {'kind': 'text', 'value': 'Summary slot live'},
                    },
                  },
                  // A bare-key slot is the view's child, read through its app's key path.
                  'local/apps/dashboard/children/orders.live' => {
                    'generation': 3,
                    'view': {
                      'title': 'Orders',
                      'root': {'kind': 'text', 'value': 'Orders slot live'},
                    },
                  },
                  'local/apps/billing/view' => {
                    'generation': 4,
                    'view': {
                      'title': 'Billing',
                      'root': {
                        'kind': 'text',
                        'value': 'Billing app slot live',
                      },
                    },
                  },
                  'local/ledger' => {
                    'generation': 5,
                    'view': {
                      'title': 'Ledger',
                      'root': {
                        'kind': 'text',
                        'value': 'Ledger unit slot live',
                      },
                    },
                  },
                  'local/metrics' => {
                    'generation': 6,
                    'view': {
                      'title': 'Metrics',
                      'root': {
                        'kind': 'text',
                        'value': 'Metrics empty-kind live',
                      },
                    },
                  },
                  _ => throw StateError('Unexpected load $kind:$name'),
                });
              },
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Summary slot live'), findsOneWidget);
      expect(find.text('Orders slot live'), findsOneWidget);
      expect(find.text('Billing app slot live'), findsOneWidget);
      expect(find.text('Ledger unit slot live'), findsOneWidget);
      expect(find.text('Metrics empty-kind live'), findsOneWidget);

      // 'browser' and 'confirmation' render display-only URI chips and never mount ViewHost.
      expect(find.text('local/browser/tab-1'), findsOneWidget);
      expect(find.text('local/confirmations'), findsOneWidget);
      expect(
        loaded,
        contains('child:local/apps/dashboard/children/orders.live'),
      );
      expect(loaded, isNot(contains('view:local/browser/tab-1')));
      expect(loaded, isNot(contains('confirmation:local/confirmations')));
    },
  );

  testWidgets(
    'nested Slot renders warm surfaceSunk skeleton while loading and ErrorLine with Retry on failure',
    (tester) async {
      final slotLoad = Completer<UiNodePayload>();
      var failSlot = false;
      var slotAttempts = 0;

      await tester.pumpWidget(
        wrap(
          ViewHost(
            kind: 'view',
            name: 'local/apps/host/view',
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              watch: (_, _) => const Stream.empty(),
              onAction: (_) async => null,
              load: (kind, name) async {
                if (name == 'local/apps/host/view') {
                  return PublishedViewPayload.fromJson({
                    'generation': 1,
                    'view': {
                      'title': 'Host',
                      'root': {
                        'kind': 'neuronRef',
                        'refKind': 'child',
                        'neuronUri': 'local/units/slow',
                      },
                    },
                  });
                }
                slotAttempts++;
                if (slotAttempts == 1) return slotLoad.future;
                if (failSlot) throw StateError('unreachable neuron');
                return PublishedViewPayload.fromJson({
                  'generation': 9,
                  'view': {
                    'title': 'Recovered',
                    'root': {'kind': 'text', 'value': 'Recovered slot content'},
                  },
                });
              },
            ),
          ),
        ),
      );
      await tester.pump();
      await tester.pump();

      // While pending, EmbedSkeleton renders with warm surfaceSunk bars (#F2EFE9 in light).
      expect(find.byType(EmbedSkeleton), findsOneWidget);
      final bars = tester
          .widgetList<Container>(
            find.descendant(
              of: find.byType(EmbedSkeleton),
              matching: find.byType(Container),
            ),
          )
          .where((w) {
            final d = w.decoration;
            return d is BoxDecoration &&
                d.color == UiColors.intochatLight.surfaceSunk;
          });
      expect(bars, isNotEmpty);

      // Complete first attempt with an error -> renders crisp ErrorLineView with Retry.
      slotLoad.completeError(StateError('offline'));
      await tester.pumpAndSettle();

      expect(find.byType(ErrorLineView), findsOneWidget);
      expect(find.text('Retry'), findsOneWidget);

      // Tap Retry -> loads slot cleanly.
      failSlot = false;
      await tester.tap(find.text('Retry'));
      await tester.pumpAndSettle();

      expect(find.text('Recovered slot content'), findsOneWidget);
    },
  );

  testWidgets(
    'clicking a button inside a nested Slot sends its command to the slot neuron, and reacts to watch signals',
    (tester) async {
      final events = <Map<String, dynamic>>[];
      final childWatch = StreamController<void>.broadcast();
      addTearDown(childWatch.close);
      var counter = 10;
      var slotGen = 42;

      await tester.pumpWidget(
        wrap(
          ViewHost(
            kind: 'view',
            name: 'local/apps/parent/view',
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              watch: (kind, name) => name == 'local/units/counter'
                  ? childWatch.stream
                  : const Stream.empty(),
              onAction: (event) async {
                events.add(event.toJson());
                if (event is ActAction &&
                    event.neuron == 'local/units/counter' &&
                    event.signal == 'counter.increment') {
                  counter++;
                  slotGen++;
                }
                return {'delivered': true};
              },
              load: (kind, name) async {
                if (name == 'local/apps/parent/view') {
                  return PublishedViewPayload.fromJson({
                    'generation': 7,
                    'view': {
                      'title': 'Parent',
                      'root': {
                        'kind': 'column',
                        'children': [
                          {'kind': 'text', 'value': 'Parent header'},
                          {
                            'kind': 'neuronRef',
                            'refKind': 'child',
                            'neuronUri': 'local/units/counter',
                          },
                        ],
                      },
                    },
                  });
                }
                if (name == 'local/units/counter') {
                  return PublishedViewPayload.fromJson({
                    'generation': slotGen,
                    'view': {
                      'title': 'Counter',
                      'root': {
                        'kind': 'row',
                        'children': [
                          {'kind': 'text', 'value': 'Count: $counter'},
                          {
                            'kind': 'button',
                            'key': 'cmd:Increment',
                            'label': 'Increment',
                            'command': {
                              'target': '.',
                              'signal': 'counter.increment',
                            },
                            'style': 'primary',
                            'enabled': true,
                          },
                        ],
                      },
                    },
                  });
                }
                throw StateError('Unknown $name');
              },
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Parent header'), findsOneWidget);
      expect(find.text('Count: 10'), findsOneWidget);

      await tester.tap(find.text('Increment'));
      await tester.pumpAndSettle();

      expect(events, hasLength(1));
      expect(events.single, {
        'kind': 'act',
        'neuron': 'local/units/counter',
        'target': '.',
        'signal': 'counter.increment',
        'args': <String, Object?>{},
      });
      expect(find.text('Count: 11'), findsOneWidget);

      // Push an external reactive signal on the child slot's watch stream.
      counter = 25;
      slotGen = 44;
      childWatch.add(null);
      await tester.pumpAndSettle();

      expect(find.text('Count: 25'), findsOneWidget);
    },
  );

  testWidgets(
    'cycle guard on neuronUri and recursion depth cap of 4 prevent infinite slot loops',
    (tester) async {
      // 1. Cycle A -> B -> A.
      await tester.pumpWidget(
        wrap(
          ViewHost(
            kind: 'view',
            name: 'local/slot-a',
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              watch: (_, _) => const Stream.empty(),
              onAction: (_) async => null,
              load: (kind, name) async =>
                  PublishedViewPayload.fromJson(switch (name) {
                    'local/slot-a' => {
                      'generation': 1,
                      'view': {
                        'title': 'A',
                        'root': {
                          'kind': 'column',
                          'children': [
                            {'kind': 'text', 'value': 'Node A'},
                            {
                              'kind': 'neuronRef',
                              'refKind': 'child',
                              'neuronUri': 'local/slot-b',
                            },
                          ],
                        },
                      },
                    },
                    'local/slot-b' => {
                      'generation': 1,
                      'view': {
                        'title': 'B',
                        'root': {
                          'kind': 'column',
                          'children': [
                            {'kind': 'text', 'value': 'Node B'},
                            {
                              'kind': 'neuronRef',
                              'refKind': 'child',
                              'neuronUri': 'local/slot-a',
                            },
                          ],
                        },
                      },
                    },
                    _ => throw StateError('Unexpected $name'),
                  }),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Node A'), findsOneWidget);
      expect(find.text('Node B'), findsOneWidget);
      expect(find.byType(EmbedGuardNotice), findsOneWidget);
      expect(find.text('This component cannot be displayed.'), findsOneWidget);

      // 2. Chain of 6 slots: depth 0 -> 1 -> 2 -> 3 -> 4 render, 5th nested slot halts at cap=4.
      await tester.pumpWidget(
        wrap(
          ViewHost(
            kind: 'view',
            name: 'local/d0',
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              watch: (_, _) => const Stream.empty(),
              onAction: (_) async => null,
              load: (kind, name) async {
                final idx = int.parse(name.substring('local/d'.length));
                return PublishedViewPayload.fromJson({
                  'generation': 1,
                  'view': {
                    'title': 'D$idx',
                    'root': {
                      'kind': 'column',
                      'children': [
                        {'kind': 'text', 'value': 'Level $idx'},
                        {
                          'kind': 'neuronRef',
                          'refKind': 'child',
                          'neuronUri': 'local/d${idx + 1}',
                        },
                      ],
                    },
                  },
                });
              },
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      for (var i = 0; i <= 4; i++) {
        expect(find.text('Level $i'), findsOneWidget);
      }
      expect(find.text('Level 5'), findsNothing);
      expect(find.text('Nested view limit reached.'), findsOneWidget);
    },
  );

  testWidgets(
    'ConversationView renders inline UI kit nodes and live interactive Slots inside chat messages',
    (tester) async {
      final events = <Map<String, dynamic>>[];

      await tester.pumpWidget(
        wrap(
          SizedBox(
            height: 640,
            child: ViewHost(
              kind: 'view',
              name: 'local/apps/ino/view',
              overrides: ViewHostOverrides(
                knowsApp: (_) => false,
                watch: (_, _) => const Stream.empty(),
                onAction: (event) async {
                  events.add(event.toJson());
                  return {'delivered': true};
                },
                load: (kind, name) async =>
                    PublishedViewPayload.fromJson(switch (name) {
                      'local/apps/ino/view' => {
                        'generation': 3,
                        'view': {
                          'title': 'Ino',
                          'root': {
                            'kind': 'conversation',
                            'composer': {
                              'key': 'composer',
                              'placeholder': 'Ask anything',
                              'enabled': true,
                            },
                            'messages': [
                              {
                                'id': 'm1',
                                'role': 'assistant',
                                'text': 'Here is the live account widget:',
                                'status': 'completed',
                                'cards': [
                                  {
                                    'kind': 'neuronRef',
                                    'refKind': 'child',
                                    'neuronUri': 'local/units/bank',
                                  },
                                ],
                              },
                            ],
                          },
                        },
                      },
                      'local/units/bank' => {
                        'generation': 12,
                        'view': {
                          'title': 'Bank',
                          'root': {
                            'kind': 'card',
                            'title': 'Checking Account',
                            'children': [
                              {
                                'kind': 'button',
                                'key': 'cmd:Deposit:50',
                                'label': 'Deposit €50',
                                'command': {
                                  'target': '.',
                                  'signal': 'bank.deposit',
                                  'args': {'amount': 50},
                                },
                                'style': 'primary',
                                'enabled': true,
                              },
                            ],
                          },
                        },
                      },
                      _ => throw StateError('Unknown $name'),
                    }),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Here is the live account widget:'), findsOneWidget);
      expect(find.text('Checking Account'), findsOneWidget);
      expect(find.text('Deposit €50'), findsOneWidget);

      await tester.tap(find.text('Deposit €50'));
      await tester.pumpAndSettle();

      expect(events, [
        {
          'kind': 'act',
          'neuron': 'local/units/bank',
          'target': '.',
          'signal': 'bank.deposit',
          'args': {'amount': 50},
        },
      ]);
    },
  );
}
