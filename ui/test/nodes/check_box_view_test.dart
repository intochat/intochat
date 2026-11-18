import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/src/generated/node_commands.dart';

NodeContext _ctx(List<Map<String, dynamic>> sent, {String slotPath = ''}) =>
    NodeContext(
      view: 'local/apps/settings/view',
      generation: 1,
      actNeuron: slotPath.isEmpty ? null : 'local/apps/settings',
      slotPath: slotPath,
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
  test('the generated senders post an act to the neuron', () async {
    final sent = <Map<String, dynamic>>[];
    final ctx = _ctx(sent, slotPath: 'terms');

    expect(await actCheckBoxToggle(ctx, '.'), ActivationOutcome.accepted);
    expect(
      await actCheckBoxSetChecked(ctx, '.', isOn: true),
      ActivationOutcome.accepted,
    );

    expect(sent, [
      {
        'kind': 'act',
        'neuron': 'local/apps/settings',
        'target': 'terms',
        'signal': 'control.toggle',
        'args': <String, Object?>{},
      },
      {
        'kind': 'act',
        'neuron': 'local/apps/settings',
        'target': 'terms',
        'signal': 'control.setChecked',
        'args': {'isOn': true},
      },
    ]);
  });

  testWidgets('a tap sends Toggle to the check box neuron', (tester) async {
    final sent = <Map<String, dynamic>>[];
    await _pump(
      tester,
      const CheckBoxViewNode(
        key: 'terms',
        label: 'I accept',
        isOn: false,
        isEnabled: true,
        automationName: 'Accept the terms',
      ),
      _ctx(sent, slotPath: 'terms'),
    );

    expect(find.bySemanticsLabel('Accept the terms'), findsOneWidget);
    await tester.tap(find.byKey(const Key('terms')));
    await tester.pumpAndSettle();

    expect(sent.single['signal'], 'control.toggle');
    expect(sent.single['target'], 'terms');
  });

  testWidgets('a disabled check box sends nothing', (tester) async {
    final sent = <Map<String, dynamic>>[];
    await _pump(
      tester,
      const CheckBoxViewNode(
        key: 'locked',
        label: 'Managed',
        isOn: true,
        isEnabled: false,
        automationName: 'Managed',
      ),
      _ctx(sent),
    );

    await tester.tap(find.byKey(const Key('locked')));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });
}
