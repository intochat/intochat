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
  testWidgets('an empty state shows its title and dispatches suggestions', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, {
      'kind': 'emptyState',
      'title': 'What do you need?',
      'body': 'Ask Ino anything about your workspace.',
      'suggestions': [
        {
          'key': 's1',
          'label': 'Summarise my day',
          'command': {
            'target': '.',
            'signal': 'send',
            'args': 'Summarise my day',
          },
        },
      ],
    }, events: events);

    expect(find.text('What do you need?'), findsOneWidget);
    expect(find.text('Ask Ino anything about your workspace.'), findsOneWidget);
    expect(find.text('Summarise my day'), findsOneWidget);

    await tester.tap(find.text('Summarise my day'));
    await tester.pumpAndSettle();
    expect(events.single['key'], 's1');
  });
}
