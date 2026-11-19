import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _props = {
  'title': 'People',
  'columns': [
    {'id': 'id', 'title': 'Id'},
    {'id': 'name', 'title': 'Name'},
  ],
  'sourceAddress': 'local/control.sampleTableSource/people',
  'isEnabled': true,
};

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  QueryTableViewNode node,
) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: SingleChildScrollView(
          child: NodeRenderer.render(
            node,
            NodeContext(
              view: 'local/apps/settings/view',
              generation: 1,
              actNeuron: 'local/apps/settings',
              slotPath: 'people',
              slotProps: _props,
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

QueryTableViewNode _node({required bool loaded, int page = 1}) =>
    QueryTableViewNode(
      key: 'people',
      filter: '',
      sortBy: null,
      descending: false,
      page: page,
      pageSize: 50,
      total: loaded ? 1 : 0,
      loaded: loaded,
      columnIds: const ['id', 'name'],
      rows: loaded
          ? const [
              ['1', 'Ada'],
            ]
          : const [],
      error: null,
    );

void main() {
  testWidgets('a view that is not loaded asks for its page once', (
    tester,
  ) async {
    final sent = await _pump(tester, _node(loaded: false, page: 3));
    await tester.pump();

    expect(find.text('Not loaded'), findsOneWidget);
    expect(sent, hasLength(1));
    expect(sent.single.toString(), contains('control.page'));
    expect(sent.single.toString(), contains('3'));
  });

  testWidgets('a loaded view sends nothing', (tester) async {
    final sent = await _pump(tester, _node(loaded: true));
    await tester.pump();

    expect(find.text('Not loaded'), findsNothing);
    expect(sent, isEmpty);
  });

  testWidgets('the table draws props columns and state rows', (tester) async {
    await _pump(
      tester,
      const QueryTableViewNode(
        key: 'people',
        filter: '',
        sortBy: 'name',
        descending: false,
        page: 1,
        pageSize: 50,
        total: 2,
        loaded: true,
        columnIds: ['id', 'name'],
        rows: [
          ['1', 'Ada'],
          ['2', 'Grace'],
        ],
        error: null,
      ),
    );

    expect(find.text('People'), findsOneWidget);
    expect(find.text('Id'), findsOneWidget);
    expect(find.text('Name ↑'), findsOneWidget);
    expect(find.text('Ada'), findsOneWidget);
    expect(find.text('Grace'), findsOneWidget);
  });

  testWidgets('a source error draws the error line, not rows', (tester) async {
    await _pump(
      tester,
      const QueryTableViewNode(
        key: 'people',
        filter: '',
        sortBy: null,
        descending: false,
        page: 1,
        pageSize: 50,
        total: 0,
        loaded: true,
        columnIds: ['id', 'name'],
        rows: [],
        error: "The table's source did not answer.",
      ),
    );

    expect(find.text("The table's source did not answer."), findsOneWidget);
    expect(find.byType(ErrorLineView), findsOneWidget);
    expect(find.text('Id'), findsNothing);
  });
}
