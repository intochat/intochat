import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _items = [
  {'id': 'laptop', 'title': 'Laptop', 'subtitle': 'Last seen today'},
  {'id': 'phone', 'title': 'Phone'},
];

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> props, {
  String? selectedId = 'laptop',
}) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: NodeRenderer.render(
          ListBoxViewNode(key: 'devices', selectedId: selectedId),
          NodeContext(
            view: 'local/apps/settings/view',
            generation: 1,
            actNeuron: 'local/apps/settings',
            slotPath: 'devices',
            slotProps: props,
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

void main() {
  testWidgets('a header is drawn where the item group changes', (tester) async {
    await _pump(tester, {
      'items': [
        {'id': 'a', 'title': 'A', 'group': 'Scenarios'},
        {'id': 'b', 'title': 'B', 'group': 'Scenarios'},
        {'id': 'c', 'title': 'C', 'group': 'Controls'},
        {'id': 'd', 'title': 'D'},
      ],
    }, selectedId: null);

    expect(find.text('Scenarios'), findsOneWidget);
    expect(find.text('Controls'), findsOneWidget);
    expect(
      tester.getTopLeft(find.text('Controls')).dy,
      greaterThan(tester.getTopLeft(find.text('B')).dy),
    );
  });

  testWidgets('a tap sends Select to the list box neuron', (tester) async {
    final sent = await _pump(tester, {'items': _items});

    expect(find.text('Last seen today'), findsOneWidget);
    await tester.tap(find.text('Phone'));
    await tester.pumpAndSettle();

    expect(sent, [
      {
        'kind': 'act',
        'neuron': 'local/apps/settings',
        'target': 'devices',
        'signal': 'control.select',
        'args': {'id': 'phone'},
      },
    ]);
  });

  testWidgets('tapping the selected item sends nothing', (tester) async {
    final sent = await _pump(tester, {'items': _items});

    await tester.tap(find.text('Laptop'));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });

  testWidgets('a list box in none mode sends nothing', (tester) async {
    final sent = await _pump(tester, {
      'items': _items,
      'selectionMode': 'None',
    });

    await tester.tap(find.text('Phone'));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });

  testWidgets('a list box with disabled props sends nothing', (tester) async {
    final sent = await _pump(tester, {'items': _items, 'isEnabled': false});

    await tester.tap(find.text('Phone'));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });

  testWidgets('a list box without props is named by its key', (tester) async {
    await _pump(tester, const {});

    expect(find.bySemanticsLabel('devices'), findsOneWidget);
  });

  testWidgets('a stale selected id selects no item and the first item sends '
      'Select', (tester) async {
    final sent = await _pump(tester, {'items': _items}, selectedId: 'watch');

    expect(
      tester
          .widgetList<ListTile>(find.byType(ListTile))
          .where((t) => t.selected),
      isEmpty,
    );
    await tester.tap(find.text('Laptop'));
    await tester.pumpAndSettle();

    expect(sent.single['args'], {'id': 'laptop'});
  });
}
