import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  testWidgets('a NoChildren refusal reaches the refusal snackbar', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.light('intochat'),
        home: Scaffold(
          body: ViewHost(
            kind: 'view',
            name: 'local/apps/notes/view',
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              watch: (_, _) => const Stream.empty(),
              onAction: (_) async => throw UiRequestException(
              'POST',
              '/act',
              409,
              '{"error":"child of local/apps/notes refused the command.",'
                  '"reason":"NoChildren"}',
            ),
              load: (_, _) async => PublishedViewPayload.fromJson({
              'generation': 1,
              'view': {
                'title': 'Notes',
                'root': {
                  'kind': 'button',
                  'key': 'toggle',
                  'label': 'Toggle',
                  'command': {'target': 'child', 'signal': 'Toggle'},
                },
              },
            }),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Toggle'));
    await tester.pumpAndSettle();

    expect(find.textContaining('NoChildren'), findsOneWidget);
    expect(find.textContaining('refused the command'), findsOneWidget);
  });
}
