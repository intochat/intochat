import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _props = {
  'fields': [
    {'key': 'name', 'label': 'Display name', 'kind': 'Text'},
    {'key': 'bio', 'label': 'Bio', 'kind': 'Multiline'},
    {'key': 'id', 'label': 'Account id', 'value': 'ada-1', 'isReadOnly': true},
    {'key': 'token', 'label': 'API key', 'kind': 'Secret'},
  ],
  'submitLabel': 'Save',
  'isEnabled': true,
};

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> props,
) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: SingleChildScrollView(
          child: NodeRenderer.render(
            const FormViewNode(
              key: 'profile',
              values: [
                FormValue(key: 'name', value: 'Ada'),
                FormValue(key: 'bio', value: 'Engineer'),
              ],
            ),
            NodeContext(
              view: 'local/apps/settings/view',
              generation: 1,
              actNeuron: 'local/apps/settings',
              slotPath: 'profile',
              slotProps: props,
              send: (event) async {
                sent.add(event);
                return {'delivered': true};
              },
            ),
          ),
        ),
      ),
    ),
  );
  return sent;
}

Map<String, dynamic> _args(Map<String, dynamic> event) =>
    Map<String, dynamic>.from(event['args'] as Map);

void main() {
  testWidgets('typing sends nothing; Submit sends every editable value once', (
    tester,
  ) async {
    final sent = await _pump(tester, _props);

    expect(find.text('Ada'), findsOneWidget);
    await tester.enterText(
      find.byKey(const ValueKey('form_field_bio')),
      'Pilot',
    );
    await tester.pump();
    expect(sent, isEmpty);

    await tester.tap(find.byKey(const ValueKey('form_submit')));
    await tester.pumpAndSettle();

    expect(sent, hasLength(1));
    expect(sent.single['signal'], 'control.submit');
    expect(sent.single['target'], 'profile');
    expect(_args(sent.single), {
      'values': {'name': 'Ada', 'bio': 'Pilot'},
    });
  });

  testWidgets('a secret field is not offered', (tester) async {
    await _pump(tester, _props);

    expect(find.text('API key'), findsNothing);
    expect(find.byKey(const ValueKey('form_field_token')), findsNothing);
  });

  testWidgets('a controlled field draws the parent value', (tester) async {
    await _pump(tester, {
      ..._props,
      'fields': [
        {'key': 'name', 'label': 'Display name', 'value': 'Grace'},
      ],
    });

    expect(find.text('Grace'), findsOneWidget);
    expect(find.text('Ada'), findsNothing);
  });

  testWidgets('disabled props send nothing', (tester) async {
    final sent = await _pump(tester, {..._props, 'isEnabled': false});

    await tester.tap(find.byKey(const ValueKey('form_submit')));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });
}
