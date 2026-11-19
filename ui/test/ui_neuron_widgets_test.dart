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

  testWidgets(
    'ChatThreadWidget expands reasoning, tool-call JSON, and triggers Compact',
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
          'version': 1,
          'conversationId': 'demo',
          'title': 'Ino DevUI',
          'activeModel': 'claude-sonnet-4-5',
          'working': false,
          'canSend': true,
          'totalInputTokens': 1240,
          'totalOutputTokens': 215,
          'contextBudgetTokens': 128000,
          'contextSummary': 'Compacted 5 turns',
          'contextItems': [
            {
              'name': 'memory.recall',
              'summary': 'Prefers WinUI 3',
              'trust': 'owner',
              'tokenEstimate': 140,
            },
          ],
          'availableModels': [
            {'id': 'claude-sonnet-4-5', 'label': 'Claude Sonnet 4.5'},
            {'id': 'gpt-5-thinking', 'label': 'GPT-5 Thinking'},
          ],
          'messages': [
            {
              'id': 'a1',
              'role': 'assistant',
              'text': 'Throughput grew +66%.',
              'reasoning': 'Inspect SampleChartSource first.',
              'status': 'done',
              'toolCalls': [
                {
                  'callId': 'tc1',
                  'name': 'ui.chart.bind',
                  'argumentsJson': '{"filter":"all"}',
                  'resultJson': '{"points":12}',
                  'status': 'done',
                  'durationMs': 14,
                },
              ],
            },
          ],
        };
      }

      await tester.pumpWidget(
        wrap(
          SingleChildScrollView(
            child: SizedBox(
              width: 640,
              child: buildUiNeuronWidget(
                kind: 'ui.chat',
                neuronKey: 'gallery/chat/devui',
                callNeuron: callNeuron,
              )!,
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Throughput grew +66%.'), findsOneWidget);
      expect(find.text('Inspect SampleChartSource first.'), findsNothing);
      expect(find.text('{"filter":"all"}'), findsNothing);

      // Expand reasoning
      await tester.tap(find.textContaining('Show reasoning'));
      await tester.pumpAndSettle();
      expect(find.text('Inspect SampleChartSource first.'), findsOneWidget);

      // Expand tool call JSON
      await tester.tap(find.text('ui.chart.bind'));
      await tester.pumpAndSettle();
      expect(find.text('{"filter":"all"}'), findsOneWidget);
      expect(find.text('{"points":12}'), findsOneWidget);

      // Click Compact
      await tester.tap(find.text('Compact'));
      await tester.pumpAndSettle();
      expect(calls, contains('IntoChat.OS.Modules.UI.Chat.IChatThread.Compact'));
    },
  );

  testWidgets('NumberBoxWidget and TreeViewWidget invoke typed neuron methods', (
    tester,
  ) async {
    final calls = <String>[];

    Future<Map<String, dynamic>?> callNeuron(
      String contract,
      String key,
      String method,
      List<Object?> args,
    ) async {
      calls.add('$contract.$method');
      if (contract.endsWith('INumberBox')) {
        return {
          'version': 1,
          'label': 'Spend Cap',
          'value': method == 'Increment' ? 60.0 : 50.0,
          'min': 10.0,
          'max': 500.0,
          'step': 10.0,
          'isEnabled': true,
          'prefix': '\$',
        };
      }
      return {
        'version': 1,
        'expandedIds': ['src'],
        'selectedId': method == 'Select' ? args.first : 'src',
        'nodes': [
          {'id': 'src', 'label': 'src', 'parentId': null, 'isFolder': true},
          {'id': 'chart', 'label': 'ChartNeuron.cs', 'parentId': 'src', 'isFolder': false},
        ],
      };
    }

    await tester.pumpWidget(
      wrap(
        Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            buildUiNeuronWidget(
              kind: 'ui.number-box',
              neuronKey: 'gallery/number/demo',
              callNeuron: callNeuron,
            )!,
            buildUiNeuronWidget(
              kind: 'ui.tree-view',
              neuronKey: 'gallery/tree-view/demo',
              callNeuron: callNeuron,
            )!,
          ],
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('\$50'), findsOneWidget);
    await tester.tap(find.byTooltip('Increment'));
    await tester.pumpAndSettle();
    expect(calls, contains('IntoChat.OS.Modules.UI.NumberBox.INumberBox.Increment'));
    expect(find.text('\$60'), findsOneWidget);

    expect(find.text('ChartNeuron.cs'), findsOneWidget);
    await tester.tap(find.text('ChartNeuron.cs'));
    await tester.pumpAndSettle();
    expect(calls, contains('IntoChat.OS.Modules.UI.TreeView.ITreeView.Select'));
  });

  testWidgets(
    'Tier 2/3 CalendarView, GridView, ImageViewer and MediaPlayer invoke gRPC methods',
    (tester) async {
      final calls = <String>[];
      var playing = false;
      var rotation = 0;

      Future<Map<String, dynamic>?> callNeuron(
        String contract,
        String key,
        String method,
        List<Object?> args,
      ) async {
        calls.add('$contract.$method');
        if (contract.endsWith('ICalendarView')) {
          return {
            'version': calls.length,
            'title': 'Sprint',
            'year': 2026,
            'month': 11,
            'selectedDates': ['2026-11-19'],
            'multiSelect': true,
          };
        }
        if (contract.endsWith('IGridView')) {
          return {
            'version': calls.length,
            'title': 'Assets',
            'selectedId': method == 'Select' ? args.first : 'arch',
            'columns': 2,
            'items': [
              {'id': 'arch', 'title': 'architecture.md', 'subtitle': '14 KB', 'badge': 'DOC'},
              {'id': 'hero', 'title': 'hero-banner.png', 'subtitle': '1.2 MB', 'badge': 'IMG'},
            ],
          };
        }
        if (contract.endsWith('IImageViewer')) {
          if (method == 'Rotate') rotation = (rotation + 90) % 360;
          return {
            'version': calls.length,
            'title': 'Preview',
            'sourceUrl': '',
            'zoom': 1.0,
            'rotationDegrees': rotation,
            'fitMode': 'contain',
          };
        }
        if (contract.endsWith('IMediaPlayer')) {
          if (method == 'Play') playing = true;
          if (method == 'Pause') playing = false;
          return {
            'version': calls.length,
            'title': 'Ambient Focus',
            'artist': 'Neural Audio',
            'playing': playing,
            'positionSeconds': 15.0,
            'durationSeconds': 180.0,
            'volume': 0.8,
            'muted': false,
          };
        }
        return {'version': 1};
      }

      await tester.pumpWidget(
        wrap(
          SingleChildScrollView(
            child: SizedBox(
              width: 560,
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  buildUiNeuronWidget(
                    kind: 'ui.calendar',
                    neuronKey: 'gallery/calendar/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.grid-view',
                    neuronKey: 'gallery/grid-view/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.image-viewer',
                    neuronKey: 'gallery/image-viewer/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.media-player',
                    neuronKey: 'gallery/media-player/demo',
                    callNeuron: callNeuron,
                  )!,
                ],
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('hero-banner.png'));
      await tester.pumpAndSettle();
      expect(calls, contains('IntoChat.OS.Modules.UI.GridView.IGridView.Select'));

      await tester.tap(find.byIcon(Icons.rotate_right));
      await tester.pumpAndSettle();
      expect(calls, contains('IntoChat.OS.Modules.UI.ImageViewer.IImageViewer.Rotate'));

      await tester.ensureVisible(find.byIcon(Icons.play_arrow));
      await tester.pumpAndSettle();
      await tester.tap(find.byIcon(Icons.play_arrow));
      await tester.pumpAndSettle();
      expect(calls, contains('IntoChat.OS.Modules.UI.MediaPlayer.IMediaPlayer.Play'));
    },
  );
}
