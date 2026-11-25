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

const _inlineProps = {
  'title': 'Crew',
  'columns': [
    {'id': 'name', 'title': 'Name'},
    {'id': 'rank', 'title': 'Rank'},
  ],
  'rows': [
    {
      'id': 'a',
      'cells': ['Ada', '10'],
    },
    {
      'id': 'g',
      'cells': ['Grace', '9'],
    },
    {
      'id': 'l',
      'cells': ['Linus', '100'],
    },
    {
      'id': 'm',
      'cells': ['Margaret', '2'],
    },
  ],
  'isEnabled': true,
};

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  QueryTableViewNode node, {
  Map<String, dynamic> props = _props,
}) async {
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
              slotProps: props,
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
      selectedRowId: null,
    );

// A table over inline rows: the view carries the query only.
QueryTableViewNode _inline({
  String filter = '',
  String? sortBy,
  bool descending = false,
  int page = 1,
  int pageSize = 50,
  bool loaded = true,
  String? selectedRowId,
}) => QueryTableViewNode(
  key: 'crew',
  filter: filter,
  sortBy: sortBy,
  descending: descending,
  page: page,
  pageSize: pageSize,
  total: 0,
  loaded: loaded,
  columnIds: const [],
  rows: const [],
  error: null,
  selectedRowId: selectedRowId,
);

List<String> _drawnNames(WidgetTester tester) => [
  for (final name in const ['Ada', 'Grace', 'Linus', 'Margaret'])
    if (find.text(name).evaluate().isNotEmpty) name,
];

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
        selectedRowId: null,
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
        selectedRowId: null,
      ),
    );

    expect(find.text("The table's source did not answer."), findsOneWidget);
    expect(find.byType(ErrorLineView), findsOneWidget);
    expect(find.text('Id'), findsNothing);
  });

  testWidgets('inline rows are filtered by the query, any cell, any case', (
    tester,
  ) async {
    await _pump(tester, _inline(filter: 'AR'), props: _inlineProps);

    expect(_drawnNames(tester), ['Margaret']);
    await _pump(tester, _inline(filter: '10'), props: _inlineProps);
    expect(_drawnNames(tester), ['Ada', 'Linus']);
  });

  testWidgets('inline rows are sorted by the query, numbers as numbers', (
    tester,
  ) async {
    await _pump(
      tester,
      _inline(sortBy: 'rank', descending: true),
      props: _inlineProps,
    );

    final ys = {
      for (final name in const ['Ada', 'Grace', 'Linus', 'Margaret'])
        name: tester.getTopLeft(find.text(name)).dy,
    };
    final order = ys.keys.toList()..sort((a, b) => ys[a]!.compareTo(ys[b]!));
    expect(order, ['Linus', 'Ada', 'Grace', 'Margaret']);
    expect(find.text('Rank ↓'), findsOneWidget);
  });

  testWidgets('inline rows are paged by the query', (tester) async {
    await _pump(tester, _inline(page: 2, pageSize: 3), props: _inlineProps);

    expect(_drawnNames(tester), ['Margaret']);
    expect(find.text('2 / 2'), findsOneWidget);
  });

  testWidgets('inline rows never ask for a page, even when not loaded', (
    tester,
  ) async {
    final sent = await _pump(
      tester,
      _inline(loaded: false),
      props: _inlineProps,
    );
    await tester.pump();

    expect(find.text('Not loaded'), findsNothing);
    expect(sent, isEmpty);
    expect(_drawnNames(tester), ['Ada', 'Grace', 'Linus', 'Margaret']);
  });

  testWidgets('tapping an inline row sends SelectRow with its id', (
    tester,
  ) async {
    final sent = await _pump(tester, _inline(), props: _inlineProps);

    await tester.tap(find.text('Grace'));
    await tester.pump();

    expect(sent, hasLength(1));
    expect(sent.single.toString(), contains('control.selectRow'));
    expect(sent.single.toString(), contains('rowId: g'));
  });

  testWidgets("the selected row is the props' when the parent owns it", (
    tester,
  ) async {
    bool selected(String id) => tester
        .widget<Semantics>(find.byKey(ValueKey('crew/row/$id')))
        .properties
        .selected!;

    await _pump(
      tester,
      _inline(selectedRowId: 'a'),
      props: {..._inlineProps, 'selectedRowId': 'l'},
    );
    expect([selected('a'), selected('l')], [false, true]);

    await _pump(tester, _inline(selectedRowId: 'a'), props: _inlineProps);
    expect([selected('a'), selected('l')], [true, false]);
  });
}
