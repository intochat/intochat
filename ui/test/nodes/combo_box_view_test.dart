import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _box = ValueKey('ui_kit_select_box');

const _props = {
  'header': 'Theme',
  'options': [
    {'id': 'light', 'label': 'Light'},
    {'id': 'dark', 'label': 'Dark'},
  ],
};

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> props, {
  String? selectedId = 'dark',
}) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: NodeRenderer.render(
          ComboBoxViewNode(key: 'theme', selectedId: selectedId),
          NodeContext(
            view: 'local/apps/settings/view',
            generation: 1,
            actNeuron: 'local/apps/settings',
            slotPath: 'theme',
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
  testWidgets('a pick sends Select to the combo box neuron', (tester) async {
    final sent = await _pump(tester, _props);

    expect(find.text('Theme'), findsOneWidget);
    expect(find.text('Dark'), findsOneWidget);
    await tester.tap(find.byKey(_box));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Light').last);
    await tester.pumpAndSettle();

    expect(sent, [
      {
        'kind': 'act',
        'neuron': 'local/apps/settings',
        'target': 'theme',
        'signal': 'control.select',
        'args': {'id': 'light'},
      },
    ]);
  });

  testWidgets('a combo box with disabled props sends nothing', (tester) async {
    final sent = await _pump(tester, {..._props, 'isEnabled': false});

    await tester.tap(find.byKey(_box));
    await tester.pumpAndSettle();
    expect(find.text('Light'), findsNothing);

    expect(sent, isEmpty);
  });

  testWidgets('a combo box without props is named by its key', (tester) async {
    await _pump(tester, const {});

    expect(find.bySemanticsLabel('theme'), findsOneWidget);
  });

  testWidgets('a stale selected id selects no option and the first option '
      'sends Select', (tester) async {
    final sent = await _pump(tester, _props, selectedId: 'sepia');

    expect(
      tester
          .widget<UiKitSelect<String>>(find.byType(UiKitSelect<String>))
          .value,
      isNull,
    );
    await tester.tap(find.byKey(_box));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Light').last);
    await tester.pumpAndSettle();

    expect(sent.single['args'], {'id': 'light'});
  });
}
