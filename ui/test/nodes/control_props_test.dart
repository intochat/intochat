import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

// A control's label, header, content and enabled are its slot's props
// (NodeContext.childProps). Without props a control draws safe defaults, and
// props never leak to the nodes nested inside a control.
NodeContext _ctx(
  List<Map<String, dynamic>> sent, [
  Map<String, dynamic> props = const {},
]) => NodeContext(
  view: 'local/apps/settings/view',
  generation: 1,
  childProps: props,
  send: (event) async {
    sent.add(event);
    return {'delivered': true};
  },
);

Future<void> _pump(WidgetTester tester, Node node, NodeContext ctx) =>
    tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(body: NodeRenderer.render(node, ctx)),
      ),
    );

void main() {
  testWidgets('a check box without props is enabled, unlabelled and named by '
      'its key', (tester) async {
    final sent = <Map<String, dynamic>>[];
    await _pump(
      tester,
      const CheckBoxViewNode(key: 'terms', isOn: false),
      _ctx(sent),
    );

    expect(find.bySemanticsLabel('terms'), findsOneWidget);
    expect(
      tester.widget<UiMixCheckbox>(find.byType(UiMixCheckbox)).onChanged,
      isNotNull,
    );
    await tester.tap(find.byKey(const Key('terms')));
    await tester.pumpAndSettle();
    expect(sent.single['signal'], 'control.setChecked');
    expect(sent.single['args'], {'isOn': true});
  });

  testWidgets('a toggle switch without props is enabled with no texts', (
    tester,
  ) async {
    final sent = <Map<String, dynamic>>[];
    await _pump(
      tester,
      const ToggleSwitchViewNode(key: 'wifi', isOn: false),
      _ctx(sent),
    );

    expect(find.text('On'), findsNothing);
    expect(find.text('Off'), findsNothing);
    await tester.tap(find.byType(UiMixToggle));
    await tester.pumpAndSettle();
    expect(sent.single['signal'], 'control.setChecked');
  });

  testWidgets('an expander without props is enabled, with an empty header '
      'and no content', (tester) async {
    final sent = <Map<String, dynamic>>[];
    await _pump(
      tester,
      const ExpanderViewNode(key: 'advanced', isExpanded: true),
      _ctx(sent),
    );

    expect(find.bySemanticsLabel('advanced'), findsOneWidget);
    await tester.tap(find.byKey(const Key('advanced')));
    await tester.pumpAndSettle();
    expect(sent.single['args'], {'isExpanded': false});
  });

  testWidgets('a control nested in expander content does not read the '
      "expander's props", (tester) async {
    final sent = <Map<String, dynamic>>[];
    await _pump(
      tester,
      const ExpanderViewNode(key: 'advanced', isExpanded: true),
      _ctx(sent, {
        'header': 'Advanced',
        'isEnabled': true,
        'label': 'Leaked label',
        'automationName': 'Leaked name',
        'children': [
          {'kind': 'checkBoxView', 'key': 'inner', 'isOn': false},
        ],
      }),
    );

    expect(find.text('Advanced'), findsOneWidget);
    expect(find.text('Leaked label'), findsNothing);
    expect(find.bySemanticsLabel('inner'), findsOneWidget);
  });
}
