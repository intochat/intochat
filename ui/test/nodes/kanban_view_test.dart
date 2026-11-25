import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _props = {
  'title': 'Work',
  'columns': [
    {'id': 'todo', 'title': 'To do'},
    {'id': 'doing', 'title': 'Doing', 'wipLimit': 2},
    {'id': 'done', 'title': 'Done'},
  ],
  'cards': [
    {'id': 'a', 'columnId': 'todo', 'title': 'Write'},
    {'id': 'b', 'columnId': 'doing', 'title': 'Ship', 'tag': 'release'},
  ],
};

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> props, {
  String? selectedCardId,
  bool refuse = false,
}) async {
  tester.view.physicalSize = const Size(1280, 900);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: NodeRenderer.render(
          KanbanViewNode(key: 'board', selectedCardId: selectedCardId),
          NodeContext(
            view: 'local/apps/tasks/view',
            generation: 1,
            actNeuron: 'local/apps/tasks',
            childPath: 'board',
            childProps: props,
            send: (event) async {
              sent.add(event);
              if (refuse) throw StateError('control.columnFull');
              return {'delivered': true};
            },
          ),
        ),
      ),
    ),
  );
  return sent;
}

Map<String, dynamic> _act(String signal, Map<String, Object?> args) => {
  'kind': 'act',
  'neuron': 'local/apps/tasks',
  'target': 'board',
  'signal': signal,
  'args': args,
};

Finder _inColumn(String column, String text) => find.descendant(
  of: find.byKey(ValueKey('board/column:$column')),
  matching: find.text(text),
);

void main() {
  testWidgets('cards are drawn in their columns from the props', (
    tester,
  ) async {
    await _pump(tester, _props);

    expect(_inColumn('todo', 'Write'), findsOneWidget);
    expect(_inColumn('doing', 'Ship'), findsOneWidget);
    expect(_inColumn('done', 'No cards'), findsOneWidget);
    expect(find.text('1/2'), findsOneWidget);
  });

  testWidgets('a tap on a card sends Select to the kanban neuron', (
    tester,
  ) async {
    final sent = await _pump(tester, _props);

    await tester.tap(find.text('Write'));
    await tester.pumpAndSettle();

    expect(sent, [
      _act('control.select', {'id': 'a'}),
    ]);
  });

  testWidgets('the parent selection wins and its card sends nothing', (
    tester,
  ) async {
    final sent = await _pump(tester, {
      ..._props,
      'selectedCardId': 'b',
    }, selectedCardId: 'a');

    await tester.tap(find.text('Ship'));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });

  testWidgets('an arrow sends MoveCard to the end of the next column', (
    tester,
  ) async {
    final sent = await _pump(tester, _props);

    await tester.tap(find.byKey(const ValueKey('right:a')));
    await tester.pumpAndSettle();

    expect(sent, [
      _act('control.moveCard', {
        'cardId': 'a',
        'columnId': 'doing',
        'index': 1,
      }),
    ]);
  });

  testWidgets('a refused move leaves the board drawn as it was', (
    tester,
  ) async {
    final sent = await _pump(tester, _props, refuse: true);

    await tester.tap(find.byKey(const ValueKey('right:a')));
    await tester.pumpAndSettle();

    expect(sent.single['signal'], 'control.moveCard');
    expect(_inColumn('todo', 'Write'), findsOneWidget);
    expect(_inColumn('doing', 'Write'), findsNothing);
  });

  testWidgets('an accepted move is drawn only when the props move the card', (
    tester,
  ) async {
    await _pump(tester, _props);

    await tester.tap(find.byKey(const ValueKey('right:a')));
    await tester.pumpAndSettle();

    expect(_inColumn('todo', 'Write'), findsOneWidget);
  });

  testWidgets('a board with disabled props sends nothing and offers no move', (
    tester,
  ) async {
    final sent = await _pump(tester, {..._props, 'isEnabled': false});

    await tester.tap(find.text('Write'));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
    expect(find.byKey(const ValueKey('right:a')), findsNothing);
  });

  testWidgets('a board without props draws no column and is named by its key', (
    tester,
  ) async {
    await _pump(tester, const {});

    expect(find.bySemanticsLabel('board'), findsOneWidget);
  });
}
