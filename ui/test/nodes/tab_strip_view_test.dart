import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart' hide Focus;
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _items = [
  {'id': 'general', 'label': 'General'},
  {'id': 'privacy', 'label': 'Privacy'},
];

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> props, {
  String? selectedId = 'general',
}) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: NodeRenderer.render(
          TabStripViewNode(key: 'sections', selectedId: selectedId),
          NodeContext(
            view: 'local/apps/settings/view',
            generation: 1,
            actNeuron: 'local/apps/settings',
            childPath: 'sections',
            childProps: props,
            send: (event) async {
              sent.add(event.toJson());
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
  testWidgets('a pick sends Select to the tab strip neuron', (tester) async {
    final sent = await _pump(tester, {'items': _items});

    expect(find.text('General'), findsOneWidget);
    await tester.tap(find.text('Privacy'));
    await tester.pumpAndSettle();

    expect(sent, [
      {
        'kind': 'act',
        'neuron': 'local/apps/settings',
        'target': 'sections',
        'signal': 'control.select',
        'args': {'id': 'privacy'},
      },
    ]);
  });

  testWidgets('the arrow keys rove the tabs and pick the next one', (
    tester,
  ) async {
    final sent = await _pump(tester, {'items': _items});

    final firstTabFocus = Focus.of(
      tester.element(find.byKey(const ValueKey('ui_mix_tab_0'))),
    );
    firstTabFocus.requestFocus();
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pumpAndSettle();

    expect(sent.last['args'], {'id': 'privacy'});
  });

  testWidgets('picking the selected tab sends nothing', (tester) async {
    final sent = await _pump(tester, {'items': _items});

    await tester.tap(find.text('General'));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });

  testWidgets('a tab strip with disabled props sends nothing', (tester) async {
    final sent = await _pump(tester, {'items': _items, 'isEnabled': false});

    await tester.tap(find.text('Privacy'));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });

  testWidgets(
    'a tab strip without props draws no tabs and is named by its key',
    (tester) async {
      await _pump(tester, const {});

      expect(find.bySemanticsLabel('sections'), findsOneWidget);
    },
  );

  testWidgets('a stale selected id selects no tab and the first tab sends '
      'Select', (tester) async {
    final sent = await _pump(tester, {'items': _items}, selectedId: 'gone');

    final tabs = tester.widget<UiMixTabs>(find.byType(UiMixTabs));
    expect(tabs.index, -1);
    await tester.tap(find.text('General'));
    await tester.pumpAndSettle();

    expect(sent.single['args'], {'id': 'general'});
  });
}
