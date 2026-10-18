import 'package:intochat_os_flutter_shell/workspace/home/app_detail_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  for (final hasEntry in [true, false]) {
    Future<dynamic> request(String method, String path, [Object? body]) async {
      if (path.endsWith('/marketplace')) return <Object>[];
      if (path.endsWith('/apps')) {
        return [
          {
            'name': 'notes',
            'title': 'Notes',
            'status': 'Live',
            'operations': hasEntry ? <String>[] : ['open'],
          },
        ];
      }
      return {
        'name': 'notes',
        'status': 'Live',
        'manifest': {
          'title': 'Notes',
          'operations': hasEntry
              ? <Object>[]
              : [
                  {'name': 'open'},
                ],
          if (hasEntry) 'entry': {'kind': 'text', 'name': 'notes'},
        },
      };
    }

    testWidgets(
      'app details opens every live neuron regardless of obsolete entry $hasEntry',
      (tester) async {
        await tester.pumpWidget(
          MaterialApp(
            home: AppDetailScreen(
              workspaceId: 'brain',
              installedName: 'notes',
              request: request,
              onOpen: (_) async {},
            ),
          ),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(const ValueKey('open-app')), findsOneWidget);
      },
    );
  }
}
