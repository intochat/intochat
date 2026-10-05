import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:digitalbrain_flutter_shell/workspace/apps/apps_screen.dart';

void main() {
  testWidgets('lists this brain\'s apps, opens a live one and deletes one', (
    tester,
  ) async {
    final server = _FakeAppsServer();
    final opened = <String>[];
    await tester.pumpWidget(
      MaterialApp(
        home: AppsScreen(
          workspaceId: 'workspace-bob',
          request: server.request,
          onClose: () {},
          onOpen: (id) async => opened.add(id),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Internet researcher'), findsOneWidget);
    expect(
      find.text('researcher · v1.2.0 · Live\nAnswers a question.'),
      findsOneWidget,
    );

    await tester.tap(find.byKey(const ValueKey('open-researcher')));
    await tester.pumpAndSettle();
    expect(opened, ['researcher']);

    // A Drafted app has no Open button.
    expect(find.byKey(const ValueKey('open-sketch')), findsNothing);

    await tester.tap(find.byKey(const ValueKey('delete-sketch')));
    await tester.pumpAndSettle();
    expect(server.calls, contains('DELETE /brains/workspace-bob/apps/sketch'));
    expect(find.text('Deleted sketch.'), findsOneWidget);
  });

  testWidgets('search filters the app list', (tester) async {
    final server = _FakeAppsServer();
    await tester.pumpWidget(
      MaterialApp(
        home: AppsScreen(
          workspaceId: 'workspace-bob',
          request: server.request,
          onClose: () {},
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const ValueKey('search-apps')),
      'researcher',
    );
    await tester.pumpAndSettle();
    expect(find.text('Internet researcher'), findsOneWidget);
    expect(find.byKey(const ValueKey('app-sketch')), findsNothing);
  });
}

class _FakeAppsServer {
  final calls = <String>[];
  var deleted = false;

  Future<dynamic> request(String method, String path, [Object? body]) async {
    calls.add('$method $path');
    if (method == 'GET' && path == '/brains/workspace-bob/apps') {
      return [
        {
          'name': 'researcher',
          'title': 'Internet researcher',
          'description': 'Answers a question.',
          'status': 'Live',
          'version': '1.2.0',
          'operations': ['ask', 'open'],
        },
        if (!deleted)
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
    if (method == 'DELETE' && path == '/brains/workspace-bob/apps/sketch') {
      deleted = true;
      return {'deleted': 'sketch'};
    }
    throw StateError('Unexpected $method $path');
  }
}
