import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _state =
    '{"balance":42,"owner":{"name":"Ada","tags":["admin","ops"]},"closed":false,"note":null}';

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  String json, {
  String title = 'State',
}) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: SingleChildScrollView(
          child: NodeRenderer.render(
            Node.fromJson({'kind': 'jsonTree', 'title': title, 'json': json}),
            NodeContext(
              view: 'local/apps/creator/view',
              generation: 1,
              actNeuron: 'local/apps/creator',
              send: (event) async {
                sent.add(event);
                return {'delivered': true};
              },
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  return sent;
}

void main() {
  testWidgets('the tree shows the top level and hides what is nested', (
    tester,
  ) async {
    await _pump(tester, _state);

    expect(find.text('State'), findsOneWidget);
    expect(find.text('balance'), findsOneWidget);
    expect(find.text('42'), findsOneWidget);
    expect(find.text('owner'), findsOneWidget);
    expect(find.text('closed'), findsOneWidget);
    expect(find.text('false'), findsOneWidget);
    expect(find.text('null'), findsOneWidget);
    expect(find.text('name'), findsNothing);
  });

  testWidgets('expanding stays in the client: nothing is sent', (tester) async {
    final sent = await _pump(tester, _state);

    await tester.tap(find.byKey(const ValueKey('json:owner')));
    await tester.pump();
    expect(find.text('name'), findsOneWidget);
    expect(find.text('"Ada"'), findsOneWidget);
    expect(find.text('tags'), findsOneWidget);
    expect(find.text('"admin"'), findsNothing);

    await tester.tap(find.byKey(const ValueKey('json:owner.tags')));
    await tester.pump();
    expect(find.text('"admin"'), findsOneWidget);
    expect(find.text('"ops"'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('json:owner')));
    await tester.pump();
    expect(find.text('name'), findsNothing);

    expect(sent, isEmpty);
  });

  testWidgets('the filter stays in the client and shows matches anywhere', (
    tester,
  ) async {
    final sent = await _pump(tester, _state);

    await tester.enterText(find.byKey(const ValueKey('json:filter')), 'ada');
    await tester.pump();
    expect(find.text('"Ada"'), findsOneWidget);
    expect(find.text('balance'), findsNothing);

    await tester.enterText(
      find.byKey(const ValueKey('json:filter')),
      'nothing',
    );
    await tester.pump();
    expect(find.text('No matching properties.'), findsOneWidget);

    await tester.enterText(find.byKey(const ValueKey('json:filter')), '');
    await tester.pump();
    expect(find.text('balance'), findsOneWidget);

    expect(sent, isEmpty);
  });

  testWidgets('text that is not JSON is shown as it is', (tester) async {
    await _pump(tester, 'not json {');

    expect(find.text('not json {'), findsOneWidget);
    expect(find.text('Not valid JSON'), findsOneWidget);
  });

  testWidgets('a new view with other JSON is drawn', (tester) async {
    await _pump(tester, '{"a":1}');
    expect(find.text('a'), findsOneWidget);

    await _pump(tester, '{"b":2}');
    expect(find.text('a'), findsNothing);
    expect(find.text('b'), findsOneWidget);
  });
}
