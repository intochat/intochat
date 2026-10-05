import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:digitalbrain_flutter_shell/workspace/apps/app_screen.dart';

// The Creator holds no request open: implement answers Building immediately and the screen
// follows the activity feed until the Builder lands.
void main() {
  testWidgets('creates a live app: author, background implement, verify', (
    tester,
  ) async {
    final server = _FakeCreatorServer();
    await tester.pumpWidget(
      MaterialApp(
        home: AppScreen(
          workspaceId: 'workspace-alice',
          request: server.request,
          pollInterval: const Duration(milliseconds: 20),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.enterText(
      find.byKey(const ValueKey('describe-app')),
      'Shout back whatever I say.',
    );
    await tester.tap(find.byKey(const ValueKey('author-app')));
    await tester.pumpAndSettle();
    expect(find.text('v0.1.0 · Drafted'), findsOneWidget);
    expect(find.textContaining('## Scenario: It shouts'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('build-app')));
    await tester.pump();
    // The implement call answered 202 Building; the screen polls until Live.
    await tester.pump(const Duration(milliseconds: 30));
    await tester.pump(const Duration(milliseconds: 30));
    await tester.pumpAndSettle();
    expect(find.text('v0.2.0 · Live'), findsOneWidget);
    expect(find.text('build finished'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('run-scenarios')));
    await tester.pumpAndSettle();
    expect(
      server.calls,
      contains('POST /brains/workspace-alice/apps/shouter/verify'),
    );
    expect(find.byKey(const ValueKey('verdict-It shouts')), findsOneWidget);
  });

  testWidgets('a green version can be published, a red one cannot', (
    tester,
  ) async {
    var green = false;
    String? published;
    final calls = <String>[];
    Future<dynamic> request(String method, String path, [Object? body]) async {
      calls.add('$method $path');
      if (method == 'POST' && path.endsWith('/publish')) {
        published = '1.0.0';
        return {'publishedVersion': published};
      }
      return {
        'name': 'shouter',
        'manifest': {'title': 'Shouter', 'description': 'Shouts back.'},
        'spec': '## Scenario: It shouts',
        'version': '1.0.0',
        'status': 'Live',
        'activity': <String>[],
        'verdicts': <Object>[],
        'versions': [
          {'version': '1.0.0', 'savedAt': '2026-10-05T00:00:00Z', 'green': green},
        ],
        'publishedVersion': published,
      };
    }

    Future<void> show() async {
      await tester.pumpWidget(
        MaterialApp(
          home: AppScreen(
            key: UniqueKey(),
            workspaceId: 'workspace-alice',
            request: request,
            name: 'shouter',
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    await show();
    expect(find.byKey(const ValueKey('publish-app')), findsNothing);

    green = true;
    await show();
    await tester.tap(find.byKey(const ValueKey('publish-app')));
    await tester.pumpAndSettle();
    expect(calls, contains('POST /brains/workspace-alice/apps/shouter/publish'));
    expect(find.text('Published v1.0.0'), findsOneWidget);
    // Already published at this version: nothing more to publish.
    expect(find.byKey(const ValueKey('publish-app')), findsNothing);
  });

  testWidgets('an installed copy says where it came from', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: AppScreen(
          workspaceId: 'workspace-alice',
          name: 'customer-researcher',
          request: (method, path, [body]) async => {
            'name': 'customer-researcher',
            'manifest': {'title': 'Customer Researcher'},
            'spec': '',
            'version': '1.2.0',
            'status': 'Live',
            'origin': {
              'publisher': 'intochat',
              'name': 'customer-researcher',
              'version': '1.2.0',
            },
          },
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(
      find.text('Installed from intochat/customer-researcher v1.2.0'),
      findsOneWidget,
    );
  });
}

class _FakeCreatorServer {
  final calls = <String>[];
  var _polls = 0;
  var _verified = false;

  Map<String, dynamic> _app(String status, String version) => {
    'name': 'shouter',
    'manifest': {'title': 'Shouter', 'description': 'Shouts back.'},
    'spec': '## Scenario: It shouts\nAsking "hello" answers "HELLO!".',
    'version': version,
    'status': status,
    'activity': status == 'Building'
        ? ['build started']
        : status == 'Live'
        ? ['build started', 'build finished']
        : <String>[],
    'verdicts': _verified
        ? [
            {'name': 'It shouts', 'passed': true, 'message': ''},
          ]
        : <Object>[],
  };

  Future<dynamic> request(String method, String path, [Object? body]) async {
    calls.add('$method $path');
    const base = '/brains/workspace-alice/apps';
    if (method == 'POST' && path == base) return _app('Drafted', '0.1.0');
    if (method == 'POST' && path == '$base/shouter/implement') {
      _polls = 0;
      return _app('Building', '0.1.0');
    }
    if (method == 'GET' && path == '$base/shouter') {
      return ++_polls < 2 ? _app('Building', '0.1.0') : _app('Live', '0.2.0');
    }
    if (method == 'POST' && path == '$base/shouter/verify') {
      _verified = true;
      return _app('Live', '0.2.0');
    }
    throw StateError('Unexpected $method $path');
  }
}
