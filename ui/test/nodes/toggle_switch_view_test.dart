import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  testWidgets('a flip sends SetChecked to the toggle switch neuron', (
    tester,
  ) async {
    final sent = <Map<String, dynamic>>[];
    final ctx = NodeContext(
      view: 'local/apps/settings/view',
      generation: 1,
      actNeuron: 'local/apps/settings',
      childPath: 'dark-mode',
      childProps: const {
        'label': 'Dark mode',
        'onContent': 'On',
        'offContent': 'Off',
      },
      send: (event) async {
        sent.add(event);
        return {'delivered': true};
      },
    );
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: NodeRenderer.render(
            const ToggleSwitchViewNode(key: 'dark-mode', isOn: false),
            ctx,
          ),
        ),
      ),
    );

    expect(find.text('Dark mode'), findsOneWidget);
    expect(find.text('Off'), findsOneWidget);
    await tester.tap(find.byType(UiMixToggle));
    await tester.pumpAndSettle();

    expect(sent, [
      {
        'kind': 'act',
        'neuron': 'local/apps/settings',
        'target': 'dark-mode',
        'signal': 'control.setChecked',
        'args': {'isOn': true},
      },
    ]);
  });

  testWidgets('a toggle switch with disabled props sends nothing', (
    tester,
  ) async {
    final sent = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: NodeRenderer.render(
            const ToggleSwitchViewNode(key: 'wifi', isOn: false),
            NodeContext(
              view: 'local/apps/settings/view',
              generation: 1,
              childProps: const {'label': 'Wi-Fi', 'isEnabled': false},
              send: (event) async {
                sent.add(event);
                return null;
              },
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.byType(UiMixToggle));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });
}
