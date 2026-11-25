import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/src/generated/node_commands.dart';

NodeContext _ctx(
  List<Map<String, dynamic>> sent, {
  String childPath = '',
  Map<String, dynamic> props = const {},
}) => NodeContext(
  view: 'local/apps/settings/view',
  generation: 1,
  actNeuron: childPath.isEmpty ? null : 'local/apps/settings',
  childPath: childPath,
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

const _parent = 'local/apps/settings/view';
const _child = 'local/apps/settings/children/terms';

// A parent view whose slot carries [props], and the check box child's own view.
Widget _host(Map<String, dynamic> props, List<Map<String, dynamic>> sent) =>
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: ViewHost(
          knowsApp: (_) => false,
          kind: 'view',
          name: _parent,
          watch: (_, _) => const Stream.empty(),
          onAction: (event) async {
            sent.add(event);
            return {'delivered': true};
          },
          load: (kind, name) async => switch (name) {
            _parent => {
              'generation': 1,
              'view': {
                'title': 'Settings',
                'root': {
                  'kind': 'column',
                  'children': [
                    {
                      'kind': 'neuronRef',
                      'refKind': 'child',
                      'neuronUri': 'terms',
                      'props': props,
                    },
                  ],
                },
              },
            },
            _child => {
              'generation': 3,
              'view': {
                'title': 'terms',
                'root': {'kind': 'checkBoxView', 'key': 'terms', 'isOn': true},
              },
            },
            _ => throw StateError('Unknown $name'),
          },
        ),
      ),
    );

void main() {
  test('the generated senders post an act to the neuron', () async {
    final sent = <Map<String, dynamic>>[];
    final ctx = _ctx(sent, childPath: 'terms');

    expect(
      await actCheckBoxSetChecked(ctx, '.', isOn: true),
      ActivationOutcome.accepted,
    );

    expect(sent, [
      {
        'kind': 'act',
        'neuron': 'local/apps/settings',
        'target': 'terms',
        'signal': 'control.setChecked',
        'args': {'isOn': true},
      },
    ]);
  });

  testWidgets('a tap sends the opposite of the drawn value as SetChecked', (
    tester,
  ) async {
    final sent = <Map<String, dynamic>>[];
    await _pump(
      tester,
      const CheckBoxViewNode(key: 'terms', isOn: false),
      _ctx(
        sent,
        childPath: 'terms',
        props: {'label': 'I accept', 'automationName': 'Accept the terms'},
      ),
    );

    expect(find.bySemanticsLabel('Accept the terms'), findsOneWidget);
    await tester.tap(find.byKey(const Key('terms')));
    await tester.pumpAndSettle();

    expect(sent.single['signal'], 'control.setChecked');
    expect(sent.single['args'], {'isOn': true});
    expect(sent.single['target'], 'terms');
  });

  testWidgets('the accessible name falls back to the label, then the key', (
    tester,
  ) async {
    await _pump(
      tester,
      const CheckBoxViewNode(key: 'terms', isOn: false),
      _ctx([], props: {'label': 'I accept'}),
    );
    expect(find.bySemanticsLabel('I accept'), findsOneWidget);

    await _pump(
      tester,
      const CheckBoxViewNode(key: 'terms', isOn: false),
      _ctx([]),
    );
    expect(find.bySemanticsLabel('terms'), findsOneWidget);
  });

  testWidgets('a slot draws the label from its props and the value from the '
      'child', (tester) async {
    final sent = <Map<String, dynamic>>[];
    await tester.pumpWidget(_host({'label': 'I accept the terms'}, sent));
    await tester.pumpAndSettle();

    expect(find.text('I accept the terms'), findsOneWidget);
    expect(
      tester.widget<UiMixCheckbox>(find.byType(UiMixCheckbox)).value,
      isTrue,
    );

    await tester.tap(find.byKey(const Key('terms')));
    await tester.pumpAndSettle();
    expect(sent.single['target'], 'terms');
    // The child draws it checked, so the tap asks for unchecked.
    expect(sent.single['signal'], 'control.setChecked');
    expect(sent.single['args'], {'isOn': false});
  });

  testWidgets(
    'a slot whose props are disabled draws disabled and posts no act',
    (tester) async {
      final sent = <Map<String, dynamic>>[];
      await tester.pumpWidget(
        _host({'label': 'Managed', 'isEnabled': false}, sent),
      );
      await tester.pumpAndSettle();

      expect(find.text('Managed'), findsOneWidget);
      expect(
        tester.widget<UiMixCheckbox>(find.byType(UiMixCheckbox)).onChanged,
        isNull,
      );
      await tester.tap(find.byKey(const Key('terms')));
      await tester.pumpAndSettle();

      expect(sent, isEmpty);
    },
  );
}
