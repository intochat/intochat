import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  Widget wrapShell(BrainSessionController session) {
    return BrainSessionScope(
      controller: session,
      child: MaterialApp(
        theme: UiTheme.dark('fluent-acrylic'),
        home: Scaffold(
          body: LiveDesktopShell(
            stylePreset: IcStylePreset.fluentAcrylic,
            barLayout: BarLayout.floatingGlass,
            paletteOpen: false,
            onTogglePalette: () {},
            onSelectPreset: (_) {},
            onCycleBarLayout: () {},
          ),
        ),
      ),
    );
  }

  BrainSessionController makeSession(IntoChatOSUiClient? client) =>
      BrainSessionController(
        brainId: 'brain-1',
        client: client,
        loadProjection: (_) async => _sampleProjection(),
        loadNode: (kind, name) async => PublishedViewPayload.fromJson({
          'generation': 1,
          'view': {
            'title': name,
            'root': {'kind': 'text', 'value': 'Viewport: $name'},
          },
        }),
        watchNode: (_, _) => const Stream.empty(),
        watchSignal: (_, _, _) => const Stream.empty(),
        readContract: (_, _, _) async => null,
        onAction: (_) async => const {},
        onActivate: (_) {},
        knowsApp: (app) => app == 'ino' || app == 'tasks',
      );

  testWidgets(
    'Shows loading state while initial notifications fetch is in flight',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final completer = Completer<http.Response>();
      final client = IntoChatOSUiClient(
        baseUri: Uri.parse('http://localhost:5000'),
        httpClient: MockClient((request) => completer.future),
      );
      final session = makeSession(client);
      addTearDown(session.dispose);

      await tester.pumpWidget(wrapShell(session));
      await tester.pump();

      await tester.tap(find.byKey(const ValueKey('os-bar-notifications')));
      await tester.pump();

      expect(
        find.byKey(const ValueKey('desktop-popover-notifications')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('popover-notifications-loading')),
        findsOneWidget,
      );
      expect(find.text('Loading notifications…'), findsOneWidget);
      expect(find.textContaining('Tripletex'), findsNothing);

      completer.complete(
        http.Response(jsonEncode({'items': [], 'unreadCount': 0}), 200),
      );
      await tester.pumpAndSettle();
      expect(find.text('No notifications yet'), findsOneWidget);
    },
  );

  testWidgets('Shows empty state when notifications have not loaded yet', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final session = makeSession(null);
    addTearDown(session.dispose);

    await tester.pumpWidget(wrapShell(session));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const ValueKey('os-bar-notifications')));
    await tester.pumpAndSettle();

    expect(
      find.byKey(const ValueKey('desktop-popover-notifications')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey('popover-notifications-empty')),
      findsOneWidget,
    );
    expect(find.text('No notifications yet'), findsOneWidget);
    expect(find.textContaining('Tripletex'), findsNothing);
  });

  testWidgets('Shows empty state when connected inbox has no notifications', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1440, 900));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://localhost:5000'),
      httpClient: MockClient((request) async {
        expect(request.url.path, '/brains/brain-1/notifications');
        return http.Response(jsonEncode({'items': [], 'unreadCount': 0}), 200);
      }),
    );
    final session = makeSession(client);
    addTearDown(session.dispose);

    await tester.pumpWidget(wrapShell(session));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const ValueKey('os-bar-notifications')));
    await tester.pumpAndSettle();

    expect(
      find.byKey(const ValueKey('desktop-popover-notifications')),
      findsOneWidget,
    );
    expect(find.text('No notifications yet'), findsOneWidget);
  });

  testWidgets(
    'Loads real notifications, marks read, marks all read, and clears',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final items = <Map<String, dynamic>>[
        {
          'id': 'n-1',
          'brainId': 'brain-1',
          'sourceApp': 'brain-1/apps/tasks',
          'title': 'Build #42 passed',
          'body': 'All 12 checks succeeded',
          'priority': 2,
          'read': false,
          'createdUtc': DateTime.now().toUtc().toIso8601String(),
        },
        {
          'id': 'n-2',
          'brainId': 'brain-1',
          'sourceApp': 'os',
          'title': 'Backup complete',
          'body': 'Snapshot sealed',
          'priority': 1,
          'read': false,
          'createdUtc': DateTime.now().toUtc().toIso8601String(),
        },
      ];
      final requests = <String>[];

      final client = IntoChatOSUiClient(
        baseUri: Uri.parse('http://localhost:5000'),
        httpClient: MockClient((request) async {
          requests.add('${request.method} ${request.url.path}');
          if (request.method == 'GET' &&
              request.url.path == '/brains/brain-1/notifications') {
            final unread = items.where((it) => it['read'] != true).length;
            return http.Response(
              jsonEncode({'items': items, 'unreadCount': unread}),
              200,
            );
          }
          if (request.method == 'POST' &&
              request.url.path == '/brains/brain-1/notifications/n-1/read') {
            items[0]['read'] = true;
            return http.Response('', 204);
          }
          if (request.method == 'POST' &&
              request.url.path == '/brains/brain-1/notifications/read-all') {
            for (final it in items) {
              it['read'] = true;
            }
            return http.Response('', 204);
          }
          if (request.method == 'POST' &&
              request.url.path == '/brains/brain-1/notifications/clear') {
            items.clear();
            return http.Response('', 204);
          }
          return http.Response('', 404);
        }),
      );
      final session = makeSession(client);
      addTearDown(session.dispose);

      await tester.pumpWidget(wrapShell(session));
      await tester.pumpAndSettle();

      // Badge shows 2 unread notifications.
      expect(
        find.descendant(
          of: find.byKey(const ValueKey('os-bar-notifications')),
          matching: find.text('2'),
        ),
        findsOneWidget,
      );

      // Open popover and verify real items render.
      await tester.tap(find.byKey(const ValueKey('os-bar-notifications')));
      await tester.pumpAndSettle();
      expect(find.text('Build #42 passed'), findsOneWidget);
      expect(find.text('Backup complete'), findsOneWidget);

      // Tap first notification -> marks read, closes popover, badge drops to 1.
      await tester.tap(find.byKey(const ValueKey('popover-notification-n-1')));
      await tester.pumpAndSettle();
      expect(requests, contains('POST /brains/brain-1/notifications/n-1/read'));
      expect(
        find.descendant(
          of: find.byKey(const ValueKey('os-bar-notifications')),
          matching: find.text('1'),
        ),
        findsOneWidget,
      );

      // Re-open popover -> tap Mark all as read -> badge clears to 0.
      await tester.tap(find.byKey(const ValueKey('os-bar-notifications')));
      await tester.pumpAndSettle();
      await tester.tap(
        find.byKey(const ValueKey('popover-notifications-mark-read')),
      );
      await tester.pumpAndSettle();
      expect(requests, contains('POST /brains/brain-1/notifications/read-all'));
      expect(
        find.descendant(
          of: find.byKey(const ValueKey('os-bar-notifications')),
          matching: find.text('1'),
        ),
        findsNothing,
      );

      // Re-open popover -> tap Clear all -> shows empty state.
      await tester.tap(find.byKey(const ValueKey('os-bar-notifications')));
      await tester.pumpAndSettle();
      await tester.tap(
        find.byKey(const ValueKey('popover-notifications-clear')),
      );
      await tester.pumpAndSettle();
      expect(requests, contains('POST /brains/brain-1/notifications/clear'));
      expect(find.text('No notifications yet'), findsOneWidget);
    },
  );
}

DesktopShellProjection _sampleProjection() => const DesktopShellProjection(
  windows: [],
  pins: ['ino'],
  tiles: [
    DesktopAppTile(
      id: 'ino',
      title: 'Ino',
      icon: 'ino',
      category: 'Core',
      dock: 1,
    ),
  ],
  openWidgets: [],
);
