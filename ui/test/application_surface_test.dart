import 'dart:convert';

import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/workspace/application_surface.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  testWidgets('the application tree chooses its declared renderer', (
    tester,
  ) async {
    final starts = <String>[];
    var conversation = false;
    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        final workspace = request.url.pathSegments[1];
        final response = {
          'generation': 1,
          'view': {
            'title': workspace,
            'root': conversation
                ? {
                    'kind': 'card',
                    'title': 'Research companion',
                    'body': '',
                    'children': <Object>[],
                  }
                : {'kind': 'text', 'value': 'The application selected text'},
          },
        };
        return http.Response(jsonEncode(response), 200);
      }),
    );
    Widget view(String workspace) => MaterialApp(
      home: Scaffold(
        body: ApplicationSurface(
          knowsApp: (app) => app == 'ino',
          client: client,
          workspace: workspace,
          application: 'assistant',
          start: () async {
            starts.add(workspace);
            return {'kind': 'view', 'name': '$workspace/root'};
          },
        ),
      ),
    );

    await tester.pumpWidget(view('one'));
    await tester.pumpAndSettle();
    expect(find.text('The application selected text'), findsOneWidget);
    expect(find.textContaining('Research companion'), findsNothing);

    conversation = true;
    await tester.pumpWidget(view('two'));
    await tester.pumpAndSettle();
    expect(find.text('Research companion'), findsOneWidget);
    expect(find.text('The application selected text'), findsNothing);
    expect(starts, ['one', 'two']);

    await tester.pumpWidget(view('two'));
    await tester.pumpAndSettle();
    expect(starts, ['one', 'two']);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a failed application start can be retried', (tester) async {
    var attempts = 0;
    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        return http.Response(
          jsonEncode({
            'generation': 1,
            'view': {
              'title': 'Ready',
              'root': {'kind': 'text', 'value': 'Ready'},
            },
          }),
          200,
        );
      }),
    );
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ApplicationSurface(
            knowsApp: (app) => app == 'ino',
            client: client,
            workspace: 'one',
            application: 'assistant',
            start: () async {
              if (++attempts == 1) throw StateError('Unavailable');
              return {'kind': 'view', 'name': 'ready'};
            },
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Retry application'), findsOneWidget);
    await tester.tap(find.text('Retry application'));
    await tester.pumpAndSettle();
    expect(find.text('Ready'), findsOneWidget);
    expect(attempts, 2);
  });
}
