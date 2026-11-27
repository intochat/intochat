import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _steps = [
  {'id': 'plan', 'label': 'Plan', 'status': 'Done'},
  {'id': 'build', 'label': 'Build', 'status': 'Active', 'description': 'Now'},
  {'id': 'ship', 'label': 'Ship', 'status': 'Pending'},
];

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> props, {
  String? activeStepId = 'build',
}) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: NodeRenderer.render(
          StepperViewNode(key: 'release', activeStepId: activeStepId),
          NodeContext(
            view: 'local/apps/tasks/view',
            generation: 1,
            actNeuron: 'local/apps/tasks',
            childPath: 'release',
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

Matcher _selected(bool selected) => isSemantics(isSelected: selected);

void main() {
  testWidgets('a tap on a step sends Select to the stepper neuron', (
    tester,
  ) async {
    final sent = await _pump(tester, {'steps': _steps, 'title': 'Release'});

    expect(find.text('Release'), findsOneWidget);
    expect(find.text('Now'), findsOneWidget);
    await tester.tap(find.text('Ship'));
    await tester.pumpAndSettle();

    expect(sent, [
      {
        'kind': 'act',
        'neuron': 'local/apps/tasks',
        'target': 'release',
        'signal': 'control.select',
        'args': {'id': 'ship'},
      },
    ]);
  });

  testWidgets('a tap on the active step sends nothing', (tester) async {
    final sent = await _pump(tester, {'steps': _steps});

    await tester.tap(find.text('Build'));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });

  testWidgets('the parent active step wins over the neuron', (tester) async {
    await _pump(tester, {'steps': _steps, 'activeStepId': 'ship'});

    expect(
      tester.getSemantics(find.bySemanticsIdentifier('step:ship')),
      _selected(true),
    );
    expect(
      tester.getSemantics(find.bySemanticsIdentifier('step:build')),
      _selected(false),
    );
  });

  testWidgets('a stepper with disabled props sends nothing', (tester) async {
    final sent = await _pump(tester, {'steps': _steps, 'isEnabled': false});

    await tester.tap(find.text('Ship'));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });
}
