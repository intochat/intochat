import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:digitalbrain_flutter_shell/workspace/apps/app_spec_view.dart';
import 'package:digitalbrain_flutter_shell/workspace/apps/create_app_screen.dart';

const _spec = '''
# Shouter

Shouts back whatever you say.

## Scenario: It shouts

Asking "hello" answers "HELLO!".
''';

void main() {
  testWidgets('shows scenario verdicts next to their headings', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: SingleChildScrollView(
            child: AppSpecView(
              spec: _spec,
              run: {
                'scenarios': [
                  {
                    'name': 'It shouts',
                    'passed': false,
                    'message': 'The answer was "hi".',
                  },
                ],
                'exitCode': 1,
              },
            ),
          ),
        ),
      ),
    );

    expect(find.text('Scenario: It shouts'), findsOneWidget);
    expect(
      find.byKey(const ValueKey('scenario-message-It shouts')),
      findsOneWidget,
    );
    expect(find.text('The answer was "hi".'), findsOneWidget);
    expect(find.text('Shouts back whatever you say.'), findsOneWidget);
  });

  testWidgets('creates a live app, implements it and runs its scenarios', (
    tester,
  ) async {
    final calls = <String>[];
    var verdicts = <Object>[];
    Map<String, dynamic> content() => {
      'manifest': {
        'title': 'Shouter',
        'runtimeName': 'prompt',
        'description': 'Shouts back.',
      },
      'files': {'app.spec.md': _spec},
      'verdicts': verdicts,
      'behaviorErrors': <String, dynamic>{},
    };
    Future<dynamic> request(String method, String path, [Object? body]) async {
      calls.add('$method $path');
      if (path == '/brains/workspace-test/creator/apps') {
        return {'owner': 'alice', 'name': 'shouter', 'content': content()};
      }
      if (path.endsWith('/creator/implement')) return content();
      if (path.endsWith('/creator/run')) {
        verdicts = [
          {'name': 'It shouts', 'passed': true, 'message': '', 'scenarioId': 's1'},
        ];
        return {'scenarios': verdicts, 'exitCode': 0};
      }
      if (path.endsWith('/creator/content')) return content();
      if (path.endsWith('/creator/save')) return {'revision': 'r1'};
      throw StateError('Unexpected $method $path');
    }

    await tester.pumpWidget(
      MaterialApp(
        home: CreateAppScreen(request: request, workspaceId: 'workspace-test'),
      ),
    );
    await tester.enterText(
      find.byKey(const ValueKey('describe-app')),
      'Shout back whatever I say',
    );
    await tester.tap(find.byKey(const ValueKey('draft-app')));
    await tester.pumpAndSettle();

    expect(find.text('Shouter · prompt app'), findsOneWidget);
    expect(find.text('Scenario: It shouts'), findsOneWidget);

    await tester.ensureVisible(find.byKey(const ValueKey('build-app')));
    await tester.tap(find.byKey(const ValueKey('build-app')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('run-scenarios')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('save-app')));
    await tester.pumpAndSettle();

    expect(calls, [
      'POST /brains/workspace-test/creator/apps',
      'POST /brains/workspace-test/packages/alice/shouter/creator/implement',
      'POST /brains/workspace-test/packages/alice/shouter/creator/run',
      'GET /brains/workspace-test/packages/alice/shouter/creator/content',
      'POST /brains/workspace-test/packages/alice/shouter/creator/save',
    ]);
    expect(find.text('Saved as alice/shouter@r1.'), findsOneWidget);
  });
}
