import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  testWidgets('viewport renders an app root without starting an operation', (
    tester,
  ) async {
    var actions = 0;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ViewHost(
            kind: 'viewport',
            name: 'canvas',
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              load: (kind, name) async => kind == 'viewport'
                  ? const ViewportPayload(
                      revision: 1,
                      root: UiViewRef(
                        kind: 'view',
                        name: 'local/apps/app/view',
                      ),
                    )
                  : PublishedViewPayload.fromJson({
                      'generation': 1,
                      'view': {
                        'title': 'App',
                        'root': {'kind': 'text', 'value': 'App-owned content'},
                      },
                    }),
              onAction: (_) async {
                actions++;
                return null;
              },
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('App-owned content'), findsOneWidget);
    expect(actions, 0);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('recursive viewport is bounded', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ViewHost(
            kind: 'viewport',
            name: 'canvas',
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              load: (_, _) async => const ViewportPayload(
                revision: 1,
                root: UiViewRef(kind: 'viewport', name: 'canvas'),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('This component cannot be displayed.'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a unit viewport shows its error above the view', (tester) async {
    var actions = 0;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ViewHost(
            kind: 'viewport',
            name: 'canvas',
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              load: (kind, name) async => kind == 'viewport'
                  ? const ViewportPayload(
                      revision: 1,
                      root: UiViewRef(
                        kind: 'view',
                        name: 'local/apps/app/view',
                      ),
                      error: 'Poll failed: 503',
                    )
                  : PublishedViewPayload.fromJson({
                      'generation': 1,
                      'view': {
                        'title': 'App',
                        'root': {'kind': 'text', 'value': 'App-owned content'},
                      },
                    }),
              onAction: (_) async {
                actions++;
                return null;
              },
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('App-owned content'), findsOneWidget);
    expect(find.text('Poll failed: 503'), findsOneWidget);
    expect(actions, 0);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets(
    'ViewHostOverrides forwards capabilities through viewport child',
    (tester) async {
      final actions = <UiAction>[];
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: ViewHost(
              kind: 'viewport',
              name: 'canvas',
              overrides: ViewHostOverrides(
                knowsApp: (_) => false,
                load: (kind, name) async => kind == 'viewport'
                    ? const ViewportPayload(
                        revision: 1,
                        root: UiViewRef(
                          kind: 'view',
                          name: 'local/apps/app/view',
                        ),
                      )
                    : PublishedViewPayload.fromJson({
                        'generation': 1,
                        'view': {
                          'title': 'App',
                          'root': {
                            'kind': 'button',
                            'key': 'go',
                            'label': 'Run',
                            'command': {'target': '.', 'signal': 'app.run'},
                          },
                        },
                      }),
                onAction: (action) async {
                  actions.add(action);
                  return null;
                },
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Run'));
      await tester.pumpAndSettle();
      expect(actions, hasLength(1));
      final act = actions.single as ActAction;
      expect(act.neuron, 'local/apps/app');
      expect(act.signal, 'app.run');
      await tester.pumpWidget(const SizedBox());
    },
  );
}
