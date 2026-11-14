import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _view = 'local/apps/ino/view';

Map<String, dynamic> _published(Map<String, dynamic> root) => {
  'generation': 3,
  'view': {'title': 'Ino', 'root': root},
};

Future<void> _host(
  WidgetTester tester,
  Map<String, dynamic> root, {
  List<Map<String, dynamic>>? events,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.light('intochat'),
      home: Scaffold(
        body: ViewHost(
          knowsApp: (_) => false,
          kind: 'view',
          name: _view,
          load: (kind, name) async => _published(root),
          watch: (kind, name) => const Stream.empty(),
          onAction: (event) async {
            events?.add(event);
            return null;
          },
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('an error line shows what happened, detail, and one action', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, {
      'kind': 'errorLine',
      'what': 'Ino could not answer.',
      'detail': 'Model provider timed out after 60s.',
      'action': {
        'key': 'retry',
        'label': 'Try again',
        'emit': {'owner': 'app.ino', 'signal': 'retry'},
      },
    }, events: events);

    expect(find.text('Ino could not answer.'), findsOneWidget);
    expect(find.text('Model provider timed out after 60s.'), findsOneWidget);
    await tester.tap(find.text('Try again'));
    await tester.pumpAndSettle();
    expect(events.single['key'], 'retry');
  });
}
