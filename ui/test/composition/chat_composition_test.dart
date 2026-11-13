import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

// A chat is a view: its transcript a column of cards, its composer a keyed field that submits to the app.
const _view = 'local/apps/ino/view';

Map<String, dynamic> _chat(List<(String, String)> entries) => {
  'kind': 'column',
  'gap': 8,
  'padding': 0,
  'children': [
    {
      'kind': 'column',
      'gap': 8,
      'padding': 0,
      'children': [
        for (final (who, text) in entries)
          {'kind': 'card', 'title': who, 'body': text, 'children': <Object>[]},
      ],
    },
    {
      'kind': 'field',
      'key': 'composer',
      'name': 'apps/ino/composer',
      'fieldKind': 'Text',
      'placeholder': 'Message Ino',
      'onSubmit': {'owner': 'apps/ino', 'signal': 'send'},
    },
  ],
};

Future<void> _host(
  WidgetTester tester,
  Map<String, dynamic> root,
  List<Map<String, dynamic>> events,
) async {
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.light('intochat'),
      home: Scaffold(
        body: ViewHost(
          knowsApp: (_) => false,
          kind: 'view',
          name: _view,
          load: (kind, name) async => kind == 'field'
              ? {'value': ''}
              : {
                  'generation': 2,
                  'view': {'title': 'Ino', 'root': root},
                },
          watch: (kind, name) => const Stream.empty(),
          onAction: (event) async {
            events.add(event);
            return null;
          },
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('the transcript renders each entry as a card', (tester) async {
    await _host(tester, _chat([('You', 'Hello'), ('Ino', 'Hi there')]), []);
    expect(find.text('You'), findsOneWidget);
    expect(find.text('Hello'), findsOneWidget);
    expect(find.text('Ino'), findsOneWidget);
    expect(find.text('Hi there'), findsOneWidget);
  });

  testWidgets('the composer submits its text through the view', (tester) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, _chat([]), events);
    await tester.enterText(find.byType(TextField), 'What is new?');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();
    expect(events.last, {
      'kind': 'view',
      'name': _view,
      'generation': 2,
      'key': 'composer',
      'value': 'What is new?',
    });
  });
}
