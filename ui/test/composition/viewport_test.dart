import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets('viewport renders an app root without starting an operation', (
    tester,
  ) async {
    var actions = 0;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ViewHost(
            knowsApp: (_) => false,
            kind: 'viewport',
            name: 'canvas',
            load: (kind, name) async => kind == 'viewport'
                ? {
                    'revision': 1,
                    'root': {'kind': 'view', 'name': 'local/apps/app/view'},
                  }
                : {
                    'generation': 1,
                    'view': {
                      'title': 'App',
                      'root': {'kind': 'text', 'value': 'App-owned content'},
                    },
                  },
            onAction: (_) async {
              actions++;
              return null;
            },
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
            knowsApp: (_) => false,
            kind: 'viewport',
            name: 'canvas',
            load: (_, _) async => {
              'revision': 1,
              'root': {'kind': 'viewport', 'name': 'canvas'},
            },
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
            knowsApp: (_) => false,
            kind: 'viewport',
            name: 'canvas',
            load: (kind, name) async => kind == 'viewport'
                ? {
                    'revision': 1,
                    'root': {'kind': 'view', 'name': 'local/apps/app/view'},
                    'error': 'Poll failed: 503',
                  }
                : {
                    'generation': 1,
                    'view': {
                      'title': 'App',
                      'root': {'kind': 'text', 'value': 'App-owned content'},
                    },
                  },
            onAction: (_) async {
              actions++;
              return null;
            },
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
}
