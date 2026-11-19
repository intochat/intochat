import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

Future<void> _pump(WidgetTester tester, Map<String, dynamic> props) async {
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: NodeRenderer.render(
          const LiveChartViewNode(
            key: 'spend',
            version: 2,
            points: [
              ChartPointNode(label: 'Mon', value: 12),
              ChartPointNode(label: 'Tue', value: 30),
            ],
          ),
          NodeContext(
            view: 'local/apps/settings/view',
            generation: 1,
            actNeuron: 'local/apps/settings',
            slotPath: 'spend',
            slotProps: props,
            send: (event) async => {'delivered': true},
          ),
        ),
      ),
    ),
  );
}

void main() {
  testWidgets('the chart view draws props title and state points', (
    tester,
  ) async {
    await _pump(tester, {'title': 'Daily spend', 'kind': 'bar'});

    expect(find.text('Daily spend'), findsWidgets);
    expect(find.byType(ChartCard), findsOneWidget);
    expect(
      tester.widget<ChartCard>(find.byType(ChartCard)).data.points.length,
      2,
    );
  });

  testWidgets('an unknown kind draws an error, not a chart', (tester) async {
    await _pump(tester, {'title': 'Odd', 'kind': 'donut'});

    expect(find.textContaining('Unknown chart kind'), findsOneWidget);
    expect(find.byType(ChartCard), findsNothing);
  });
}
