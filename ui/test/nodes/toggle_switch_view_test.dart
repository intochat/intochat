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
      slotPath: 'dark-mode',
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
            const ToggleSwitchViewNode(
              key: 'dark-mode',
              label: 'Dark mode',
              isOn: false,
              isEnabled: true,
              automationName: 'Dark mode',
              onContent: 'On',
              offContent: 'Off',
            ),
            ctx,
          ),
        ),
      ),
    );

    expect(find.text('Off'), findsOneWidget);
    await tester.tap(find.byType(UiKitSwitch));
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

  testWidgets('a disabled toggle switch sends nothing', (tester) async {
    final sent = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: NodeRenderer.render(
            const ToggleSwitchViewNode(
              key: 'wifi',
              label: 'Wi-Fi',
              isOn: false,
              isEnabled: false,
              automationName: 'Wi-Fi',
              onContent: null,
              offContent: null,
            ),
            NodeContext(
              view: 'local/apps/settings/view',
              generation: 1,
              send: (event) async {
                sent.add(event);
                return null;
              },
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.byType(UiKitSwitch));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });
}
