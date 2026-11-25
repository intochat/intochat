import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

// A controlled control's props carry the value the parent owns: the view draws
// it instead of the neuron's own state.
Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Node node,
  String slot,
  Map<String, dynamic> props,
) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: NodeRenderer.render(
          node,
          NodeContext(
            view: 'local/apps/settings/view',
            generation: 1,
            actNeuron: 'local/apps/settings',
            childPath: slot,
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

void main() {
  testWidgets('a controlled switch draws props.isOn and asks for its flip', (
    tester,
  ) async {
    final sent = await _pump(
      tester,
      const ToggleSwitchViewNode(key: 'dark', isOn: false),
      'dark',
      const {
        'label': 'Dark',
        'onContent': 'On',
        'offContent': 'Off',
        'isOn': true,
      },
    );

    expect(find.text('On'), findsOneWidget);
    await tester.tap(find.byType(UiMixToggle));
    await tester.pumpAndSettle();

    expect(sent.single['args'], {'isOn': false});
  });

  testWidgets('a controlled check box draws props.isOn', (tester) async {
    await _pump(
      tester,
      const CheckBoxViewNode(key: 'terms', isOn: false),
      'terms',
      const {'label': 'Terms', 'isOn': true},
    );

    final box = tester.widget<UiMixCheckbox>(find.byType(UiMixCheckbox));
    expect(box.value, isTrue);
  });

  testWidgets('a controlled combo box draws props.selectedId', (tester) async {
    await _pump(
      tester,
      const ComboBoxViewNode(key: 'theme', selectedId: 'light'),
      'theme',
      const {
        'header': 'Theme',
        'options': [
          {'id': 'light', 'label': 'Light'},
          {'id': 'dark', 'label': 'Dark'},
        ],
        'selectedId': 'dark',
      },
    );

    expect(find.text('Dark'), findsOneWidget);
    expect(find.text('Light'), findsNothing);
  });
}
