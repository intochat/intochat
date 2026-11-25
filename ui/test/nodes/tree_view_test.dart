import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _nodes = [
  {'id': 'src', 'label': 'src', 'isFolder': true},
  {'id': 'app', 'label': 'app.cs', 'parentId': 'src'},
  {'id': 'docs', 'label': 'docs'},
  {'id': 'guide', 'label': 'guide.md', 'parentId': 'docs'},
  {'id': 'readme', 'label': 'README.md'},
];

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> props, {
  String? selectedId,
  List<String> expandedIds = const [],
}) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: NodeRenderer.render(
          TreeViewNode(
            key: 'files',
            selectedId: selectedId,
            expandedIds: expandedIds,
          ),
          NodeContext(
            view: 'local/apps/engineer/view',
            generation: 1,
            actNeuron: 'local/apps/engineer',
            childPath: 'files',
            childProps: props,
            send: (event) async {
              sent.add(event);
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
  'neuron': 'local/apps/engineer',
  'target': 'files',
  'signal': signal,
  'args': args,
};

void main() {
  testWidgets('a collapsed folder hides its nodes; the neuron expands it', (
    tester,
  ) async {
    await _pump(tester, {'nodes': _nodes});
    expect(find.text('src'), findsOneWidget);
    expect(find.text('app.cs'), findsNothing);

    await _pump(tester, {'nodes': _nodes}, expandedIds: ['src']);
    expect(find.text('app.cs'), findsOneWidget);
  });

  testWidgets('a chevron sends ExpandNode with the opposite state', (
    tester,
  ) async {
    final sent = await _pump(tester, {'nodes': _nodes}, expandedIds: ['src']);

    await tester.tap(find.byKey(const ValueKey('chevron:src')));
    await tester.tap(find.byKey(const ValueKey('chevron:docs')));
    await tester.pumpAndSettle();

    expect(sent, [
      _act('control.expandNode', {'id': 'src', 'isExpanded': false}),
      _act('control.expandNode', {'id': 'docs', 'isExpanded': true}),
    ]);
  });

  testWidgets('a node that holds nodes is a folder; a leaf has no chevron', (
    tester,
  ) async {
    await _pump(tester, {'nodes': _nodes});

    expect(find.byKey(const ValueKey('chevron:docs')), findsOneWidget);
    expect(find.byKey(const ValueKey('chevron:readme')), findsNothing);
  });

  testWidgets('a tap on a node sends Select; the selected node sends nothing', (
    tester,
  ) async {
    final sent = await _pump(tester, {'nodes': _nodes}, selectedId: 'readme');

    await tester.tap(find.text('README.md'));
    await tester.tap(find.text('src'));
    await tester.pumpAndSettle();

    expect(sent, [
      _act('control.select', {'id': 'src'}),
    ]);
  });

  testWidgets('the parent can open the tree to the node it selected', (
    tester,
  ) async {
    await _pump(tester, {
      'nodes': _nodes,
      'selectedId': 'guide',
      'expandedIds': ['docs'],
    });

    expect(find.text('guide.md'), findsOneWidget);
    expect(find.text('app.cs'), findsNothing);
    expect(
      tester.getSemantics(find.bySemanticsIdentifier('node:guide')),
      isSemantics(isSelected: true),
    );
  });

  testWidgets('parent ids that form a cycle draw each node once', (
    tester,
  ) async {
    await _pump(
      tester,
      {
        'nodes': [
          {'id': 'top', 'label': 'top', 'isFolder': true},
          {'id': 'x', 'label': 'x', 'parentId': 'top'},
          {'id': 'y', 'label': 'y', 'parentId': 'x'},
          {'id': 'x2', 'label': 'x again', 'parentId': 'y'},
        ],
      },
      expandedIds: ['top', 'x', 'y', 'x2'],
    );

    expect(tester.takeException(), isNull);
    expect(find.text('y'), findsOneWidget);
  });

  testWidgets('a tree with disabled props sends nothing', (tester) async {
    final sent = await _pump(tester, {'nodes': _nodes, 'isEnabled': false});

    await tester.tap(find.text('README.md'));
    await tester.tap(find.byKey(const ValueKey('chevron:src')));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });
}
