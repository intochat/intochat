import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  Future<List<Map<String, dynamic>>> pump(
    WidgetTester tester, {
    required bool isExpanded,
    bool isEnabled = true,
  }) async {
    final sent = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: NodeRenderer.render(
            ExpanderViewNode(key: 'advanced', isExpanded: isExpanded),
            NodeContext(
              view: 'local/apps/settings/view',
              generation: 1,
              childProps: {
                'header': 'Advanced',
                'isEnabled': isEnabled,
                'automationName': 'Advanced options',
                'children': [
                  {'kind': 'text', 'value': 'Inside'},
                ],
              },
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

  testWidgets('a collapsed expander hides its children and asks to expand', (
    tester,
  ) async {
    final sent = await pump(tester, isExpanded: false);

    expect(find.text('Inside'), findsNothing);
    await tester.tap(find.text('Advanced'));
    await tester.pumpAndSettle();

    expect(sent.single['signal'], 'control.expand');
    expect(sent.single['target'], '.');
    expect(sent.single['args'], {'isExpanded': true});
  });

  testWidgets('an expanded expander shows its children and asks to collapse', (
    tester,
  ) async {
    final sent = await pump(tester, isExpanded: true);

    expect(find.text('Inside'), findsOneWidget);
    await tester.tap(find.text('Advanced'));
    await tester.pumpAndSettle();

    expect(sent.single['args'], {'isExpanded': false});
  });

  testWidgets('an expander with disabled props sends nothing', (tester) async {
    final sent = await pump(tester, isExpanded: false, isEnabled: false);

    await tester.tap(find.text('Advanced'));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });
}
