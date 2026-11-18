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

Future<void> _pump(WidgetTester tester, QueryTableViewNode node) async {
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
              send: (event) async => {'delivered': true},
            ),
          ),
        ),
      ),
    ),
  );
}

void main() {
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
