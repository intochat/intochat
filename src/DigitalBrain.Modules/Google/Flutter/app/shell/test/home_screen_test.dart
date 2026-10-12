import 'package:digitalbrain_flutter_shell/workspace/home/app_detail_screen.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/home_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

class _FakeHome {
  final calls = <String>[];
  Future<dynamic> request(String method, String path, [Object? body]) async {
    calls.add('$method $path');
    if (path == '/brains/b1/apps/assistant') {
      return {
        'manifest': {
          'entry': {'kind': 'surface', 'name': 'surface'},
        },
      };
    }
    if (path == '/brains/b1/apps') {
      return [
        {
          'name': 'assistant',
          'title': 'Assistant',
          'description': 'Chat with your brain',
          'status': 'Live',
          'version': '1.0.0',
          'operations': ['open'],
          'origin': {
            'publisher': 'os',
            'name': 'assistant',
            'version': '1.0.0',
          },
          'edited': false,
          'updateAvailable': true,
        },
        {
          'name': 'sketch',
          'title': 'Sketch',
          'description': '',
          'status': 'Drafted',
          'version': '0.1.0',
          'operations': ['open'],
        },
      ];
    }
    if (path == '/brains/b1/marketplace') {
      return [
        {
          'listing': {
            'publisher': 'intochat',
            'source': {
              r'$source': 'brain',
              'accountId': 'acc-1',
              'brainId': 'account-acc-1',
            },
            'name': 'customer-researcher',
            'title': 'Customer Researcher',
            'description': 'Research a company',
            'version': '1.3.0',
            'icon': null,
          },
          'installed': false,
          'updateAvailable': false,
        },
        {
          'listing': {
            'publisher': 'os',
            'source': {
              r'$source': 'brain',
              'accountId': 'acc-os',
              'brainId': 'account-acc-os',
            },
            'name': 'assistant',
            'title': 'Assistant',
            'description': 'Chat with your brain',
            'version': '1.1.0',
            'icon': null,
          },
          'installed': true,
          'updateAvailable': true,
        },
      ];
    }
    if (path == '/brains/b1/marketplace/intochat/customer-researcher') {
      return (await request('GET', '/brains/b1/marketplace') as List).first;
    }
    throw StateError('Unexpected $method $path');
  }
}

void main() {
  testWidgets('my apps come first, then the Marketplace', (tester) async {
    final server = _FakeHome();
    final opened = <String>[];
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: HomeScreen(
            workspaceId: 'b1',
            request: server.request,
            onOpen: (name) async => opened.add(name),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    final myApps = tester.getTopLeft(
      find.byKey(const ValueKey('home-my-apps')),
    );
    final market = tester.getTopLeft(
      find.byKey(const ValueKey('home-marketplace')),
    );
    expect(myApps.dy, lessThan(market.dy));
    expect(find.byKey(const ValueKey('app-assistant')), findsOneWidget);
    expect(
      find.byKey(const ValueKey('market-intochat/customer-researcher')),
      findsOneWidget,
    );
    expect(find.text('Update'), findsOneWidget);
    expect(find.text('Installed'), findsOneWidget);

    // A live app with a declared entry opens on tap.
    await tester.tap(find.byKey(const ValueKey('app-assistant')));
    await tester.pumpAndSettle();
    expect(opened, ['assistant']);

    // A drafted app has nothing to open; its tile leads to its details.
    await tester.tap(find.byKey(const ValueKey('app-sketch')));
    await tester.pumpAndSettle();
    expect(opened, ['assistant']);
    expect(find.byType(AppDetailScreen), findsOneWidget);
  });

  testWidgets('decorateIcon dresses an installed app icon', (tester) async {
    final server = _FakeHome();
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: HomeScreen(
            workspaceId: 'b1',
            request: server.request,
            onOpen: (_) async {},
            decorateIcon: (app, icon) => app.id == 'sketch'
                ? Stack(children: [icon, const Text('dressed')])
                : icon,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('dressed'), findsOneWidget);
    expect(
      find.descendant(
        of: find.byKey(const ValueKey('app-sketch')),
        matching: find.text('dressed'),
      ),
      findsOneWidget,
    );
  });

  testWidgets('a marketplace tile opens its detail page', (tester) async {
    final server = _FakeHome();
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: HomeScreen(
            workspaceId: 'b1',
            request: server.request,
            onOpen: (_) async {},
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(
      find.byKey(const ValueKey('market-intochat/customer-researcher')),
    );
    await tester.pumpAndSettle();
    final detail = tester.widget<AppDetailScreen>(find.byType(AppDetailScreen));
    expect(detail.listing!.name, 'customer-researcher');
    expect(find.byKey(const ValueKey('install-app')), findsOneWidget);
  });

  testWidgets('an offline shell shows only its local apps', (tester) async {
    final launched = <String>[];
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: HomeScreen(
            workspaceId: 'local',
            onOpen: (_) async {},
            localApps: const [
              LocalApp(id: 'csharp', title: 'C# files', icon: Icons.code),
            ],
            onLaunchLocal: launched.add,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('home-marketplace')), findsNothing);
    expect(find.byKey(const ValueKey('create-app')), findsNothing);
    await tester.tap(find.byKey(const ValueKey('local-csharp')));
    expect(launched, ['csharp']);
  });

  testWidgets('a failed load says so and keeps the local apps', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: HomeScreen(
            workspaceId: 'b1',
            request: (method, path, [body]) async =>
                throw StateError('kernel unreachable'),
            onOpen: (_) async {},
            localApps: const [
              LocalApp(id: 'csharp', title: 'C# files', icon: Icons.code),
            ],
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('home-error')), findsOneWidget);
    expect(find.byKey(const ValueKey('local-csharp')), findsOneWidget);
  });

  testWidgets(
    'running apps come first with a live preview; tap focuses, close stops',
    (tester) async {
      final server = _FakeHome();
      final focused = <String>[], stopped = <String>[];
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: HomeScreen(
              workspaceId: 'b1',
              request: server.request,
              onOpen: (_) async {},
              running: const [
                RunningApp(
                  windowId: 'b1/apps/calculator/window',
                  app: 'calculator',
                  title: 'Calculator',
                ),
              ],
              surfaceBuilder: (app) => Text(
                'surface of ${app.app}',
                key: ValueKey('preview-${app.app}'),
              ),
              onFocus: focused.add,
              onStop: stopped.add,
              infrastructure: const Text('strip', key: ValueKey('fake-strip')),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      final running = tester.getTopLeft(
        find.byKey(const ValueKey('home-running')),
      );
      final strip = tester.getTopLeft(
        find.byKey(const ValueKey('home-infrastructure')),
      );
      final myApps = tester.getTopLeft(
        find.byKey(const ValueKey('home-my-apps')),
      );
      expect(running.dy, lessThan(strip.dy));
      expect(strip.dy, lessThan(myApps.dy));
      expect(find.byKey(const ValueKey('preview-calculator')), findsOneWidget);
      expect(find.byKey(const ValueKey('fake-strip')), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('stop-calculator')));
      expect(stopped, ['b1/apps/calculator/window']);
      await tester.tap(find.text('Calculator'));
      expect(focused, ['b1/apps/calculator/window']);
    },
  );
}
