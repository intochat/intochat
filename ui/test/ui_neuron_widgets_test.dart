import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  Widget wrap(Widget child) {
    return MaterialApp(
      theme: ThemeData(
        useMaterial3: true,
        extensions: [IcColors.forPreset(IcStylePreset.warmPaper, dark: false)],
      ),
      home: Scaffold(body: Center(child: child)),
    );
  }

  testWidgets('ToggleWidget reads IToggle state and invokes Toggle on tap', (
    tester,
  ) async {
    var isOn = false;
    final calls = <String>[];
    final controller = StreamController<void>.broadcast();

    Future<Map<String, dynamic>?> callNeuron(
      String contract,
      String key,
      String method,
      List<Object?> args,
    ) async {
      calls.add('$contract.$method');
      if (method == 'Toggle') {
        isOn = !isOn;
        controller.add(null);
      }
      return {
        'version': calls.length,
        'label': 'Live telemetry',
        'isOn': isOn,
        'isEnabled': true,
        'onLabel': 'Active',
        'offLabel': 'Paused',
      };
    }

    await tester.pumpWidget(
      wrap(
        buildUiNeuronWidget(
          kind: 'ui.toggle',
          neuronKey: 'gallery/toggles/wifi',
          callNeuron: callNeuron,
          watch: (_, _) => controller.stream,
        )!,
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Live telemetry'), findsOneWidget);
    expect(find.text('Paused'), findsOneWidget);

    await tester.tap(find.byType(UiKitSwitch));
    await tester.pumpAndSettle();

    expect(calls, contains('IntoChat.OS.Modules.UI.Toggle.IToggle.Toggle'));
    expect(find.text('Active'), findsOneWidget);

    await controller.close();
  });

  testWidgets(
    'ChartWidget reads IChart state, renders docs and selected point',
    (tester) async {
      final calls = <String>[];

      Future<Map<String, dynamic>?> callNeuron(
        String contract,
        String key,
        String method,
        List<Object?> args,
      ) async {
        calls.add('$contract.$method');
        return {
          'version': 2,
          'title': 'Quarterly Throughput',
          'description': 'Backed by SampleChartSource.',
          'kind': 'columns',
          'from': 'Telemetry',
          'prefix': '',
          'status': 'ready',
          'selectedPoint': {
            'label': 'Feb',
            'seriesName': '2026',
            'value': 82.0,
          },
          'series': [
            {
              'name': '2026',
              'points': [
                {'label': 'Jan', 'value': 68.0},
                {'label': 'Feb', 'value': 82.0},
              ],
            },
            {
              'name': '2025',
              'points': [
                {'label': 'Jan', 'value': 54.0},
                {'label': 'Feb', 'value': 62.0},
              ],
            },
          ],
        };
      }

      await tester.pumpWidget(
        wrap(
          SizedBox(
            width: 520,
            child: buildUiNeuronWidget(
              kind: 'ui.chart',
              neuronKey: 'gallery/charts/revenue',
              callNeuron: callNeuron,
            )!,
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Quarterly Throughput'), findsOneWidget);
      expect(find.text('Backed by SampleChartSource.'), findsOneWidget);
      expect(find.textContaining('Selected: Feb'), findsOneWidget);
      expect(calls, contains('IntoChat.OS.Modules.UI.Chart.IChart.Read'));
    },
  );
}
