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
      expect(
        calls,
        contains('IntoChat.OS.Modules.UI.Chat.IChatThread.Compact'),
      );
    },
  );

  testWidgets(
    'NumberBoxWidget and TreeViewWidget invoke typed neuron methods',
    (tester) async {
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
            {
              'id': 'chart',
              'label': 'ChartNeuron.cs',
              'parentId': 'src',
              'isFolder': false,
            },
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
      expect(
        calls,
        contains('IntoChat.OS.Modules.UI.NumberBox.INumberBox.Increment'),
      );
      expect(find.text('\$60'), findsOneWidget);

      expect(find.text('ChartNeuron.cs'), findsOneWidget);
      await tester.tap(find.text('ChartNeuron.cs'));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains('IntoChat.OS.Modules.UI.TreeView.ITreeView.Select'),
      );
    },
  );

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
              {
                'id': 'arch',
                'title': 'architecture.md',
                'subtitle': '14 KB',
                'badge': 'DOC',
              },
              {
                'id': 'hero',
                'title': 'hero-banner.png',
                'subtitle': '1.2 MB',
                'badge': 'IMG',
              },
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
      expect(
        calls,
        contains('IntoChat.OS.Modules.UI.GridView.IGridView.Select'),
      );

      await tester.tap(find.byIcon(Icons.rotate_right));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains('IntoChat.OS.Modules.UI.ImageViewer.IImageViewer.Rotate'),
      );

      await tester.ensureVisible(find.byIcon(Icons.play_arrow));
      await tester.pumpAndSettle();
      await tester.tap(find.byIcon(Icons.play_arrow));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains('IntoChat.OS.Modules.UI.MediaPlayer.IMediaPlayer.Play'),
      );
    },
  );

  testWidgets(
    'Wave 2 CommandBar, SplitButton, PersonPicture and TeachingTip invoke gRPC methods',
    (tester) async {
      final calls = <String>[];
      var tipOpen = true;

      Future<Map<String, dynamic>?> callNeuron(
        String contract,
        String key,
        String method,
        List<Object?> args,
      ) async {
        calls.add('$contract.$method');
        if (contract.endsWith('ICommandBar')) {
          return {
            'version': calls.length,
            'items': [
              {'id': 'run', 'label': 'Run', 'icon': 'play', 'primary': true},
            ],
            'lastInvokedId': method == 'Invoke' ? args.first : null,
            'invokeCount': method == 'Invoke' ? 1 : 0,
          };
        }
        if (contract.endsWith('ISplitButton')) {
          return {
            'version': calls.length,
            'primaryLabel': 'Build & Test',
            'primaryIcon': 'play',
            'options': [
              {'id': 'build-test', 'label': 'Build & Test'},
            ],
            'invokeCount': method == 'InvokePrimary' ? 1 : 0,
          };
        }
        if (contract.endsWith('IPersonPicture')) {
          return {
            'version': 1,
            'displayName': 'Ada Lovelace',
            'initials': 'AL',
            'presence': 'online',
            'subtitle': 'Architect',
          };
        }
        if (contract.endsWith('ITeachingTip')) {
          if (method == 'InvokeAction' || method == 'Dismiss') tipOpen = false;
          return {
            'version': calls.length,
            'title': 'Direct gRPC Neurons',
            'subtitle': 'All 34 UI controls are live.',
            'isOpen': tipOpen,
            'actionLabel': 'Explore Charts',
            'actionCount': method == 'InvokeAction' ? 1 : 0,
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
                    kind: 'ui.command-bar',
                    neuronKey: 'gallery/command-bar/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.split-button',
                    neuronKey: 'gallery/split-button/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.person-picture',
                    neuronKey: 'gallery/person/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.teaching-tip',
                    neuronKey: 'gallery/teaching-tip/demo',
                    callNeuron: callNeuron,
                  )!,
                ],
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Run'));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains('IntoChat.OS.Modules.UI.CommandBar.ICommandBar.Invoke'),
      );

      await tester.tap(find.text('Build & Test'));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains(
          'IntoChat.OS.Modules.UI.SplitButton.ISplitButton.InvokePrimary',
        ),
      );

      expect(find.text('Ada Lovelace'), findsOneWidget);

      await tester.tap(find.text('Explore Charts'));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains(
          'IntoChat.OS.Modules.UI.TeachingTip.ITeachingTip.InvokeAction',
        ),
      );
    },
  );

  testWidgets(
    'Wave 3 Stepper, PropertyGrid, PipsPager, InfoBadge, Toast, FilePicker, ToggleSplitButton and Kanban invoke gRPC methods',
    (tester) async {
      final calls = <String>[];

      Future<Map<String, dynamic>?> callNeuron(
        String contract,
        String key,
        String method,
        List<Object?> args,
      ) async {
        calls.add('$contract.$method');
        if (contract.endsWith('IStepper')) {
          return {
            'version': calls.length,
            'title': 'Engineer Pipeline',
            'activeStepId': 'code',
            'allowInteractiveSelection': true,
            'steps': [
              {'id': 'plan', 'title': 'Plan', 'status': 'done'},
              {'id': 'code', 'title': 'Implement', 'status': 'active'},
            ],
          };
        }
        if (contract.endsWith('IPropertyGrid')) {
          return {
            'version': calls.length,
            'title': 'Live State',
            'readOnly': true,
            'filter': '',
            'expandedPaths': const <String>[],
            'entries': [
              {
                'path': 'version',
                'name': 'version',
                'value': '3',
                'valueKind': 'number',
                'depth': 0,
                'hasChildren': false,
              },
            ],
          };
        }
        if (contract.endsWith('IPipsPager')) {
          return {
            'version': calls.length,
            'pageCount': 4,
            'selectedIndex': method == 'Next' ? 1 : 0,
            'maxVisiblePips': 4,
            'orientation': 'horizontal',
            'showPrevNext': true,
          };
        }
        if (contract.endsWith('IInfoBadge')) {
          return {
            'version': 1,
            'label': 'Reviews',
            'value': 7,
            'severity': 'attention',
            'kind': 'count',
            'isVisible': true,
          };
        }
        if (contract.endsWith('INotificationToast')) {
          return {
            'version': calls.length,
            'isOpen': method != 'InvokeAction',
            'items': method == 'InvokeAction'
                ? const []
                : [
                    {
                      'id': 't1',
                      'title': 'Build Ready',
                      'message': 'All green',
                      'severity': 'success',
                      'actionLabel': 'Open Report',
                    },
                  ],
          };
        }
        if (contract.endsWith('IFilePicker')) {
          return {
            'version': calls.length,
            'title': 'Pick File',
            'mode': 'open',
            'currentPath': '/workspace',
            'activeFilter': '*',
            'filters': ['*', '.cs'],
            'selectedPaths': ['/workspace/App.cs'],
            'entries': [
              {
                'path': '/workspace/App.cs',
                'name': 'App.cs',
                'isDirectory': false,
                'sizeBytes': 2048,
              },
            ],
          };
        }
        if (contract.endsWith('IToggleSplitButton')) {
          return {
            'version': calls.length,
            'label': 'Live Watch',
            'isChecked': method == 'Toggle',
            'isEnabled': true,
            'options': [
              {'id': 'all', 'label': 'Watch All'},
            ],
          };
        }
        if (contract.endsWith('IKanbanBoard')) {
          return {
            'version': calls.length,
            'title': 'Sprint Board',
            'selectedCardId': 'c1',
            'columns': [
              {'id': 'todo', 'title': 'Backlog'},
              {'id': 'done', 'title': 'Done'},
            ],
            'cards': [
              {
                'id': 'c1',
                'columnId': method == 'MoveCard' ? 'done' : 'todo',
                'title': 'Wire Stepper',
                'order': 0,
              },
            ],
          };
        }
        return {'version': 1};
      }

      await tester.pumpWidget(
        wrap(
          SingleChildScrollView(
            child: SizedBox(
              width: 680,
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  buildUiNeuronWidget(
                    kind: 'ui.stepper',
                    neuronKey: 'gallery/stepper/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.property-grid',
                    neuronKey: 'gallery/property-grid/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.pips-pager',
                    neuronKey: 'gallery/pips-pager/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.info-badge',
                    neuronKey: 'gallery/info-badge/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.toast',
                    neuronKey: 'gallery/toast/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.file-picker',
                    neuronKey: 'gallery/file-picker/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.toggle-split-button',
                    neuronKey: 'gallery/toggle-split-button/demo',
                    callNeuron: callNeuron,
                  )!,
                  buildUiNeuronWidget(
                    kind: 'ui.kanban',
                    neuronKey: 'gallery/kanban/demo',
                    callNeuron: callNeuron,
                  )!,
                ],
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Advance'));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains('IntoChat.OS.Modules.UI.Stepper.IStepper.Advance'),
      );

      expect(find.text('Reviews'), findsOneWidget);
      expect(find.text('7'), findsOneWidget);

      await tester.tap(find.byTooltip('Next page'));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains('IntoChat.OS.Modules.UI.PipsPager.IPipsPager.Next'),
      );

      await tester.tap(find.text('Open Report'));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains(
          'IntoChat.OS.Modules.UI.NotificationToast.INotificationToast.InvokeAction',
        ),
      );

      await tester.ensureVisible(find.text('Open'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Open'));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains('IntoChat.OS.Modules.UI.FilePicker.IFilePicker.Confirm'),
      );

      await tester.ensureVisible(find.text('Live Watch'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Live Watch'));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains(
          'IntoChat.OS.Modules.UI.ToggleSplitButton.IToggleSplitButton.Toggle',
        ),
      );

      await tester.ensureVisible(find.text('Done'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Done'));
      await tester.pumpAndSettle();
      expect(
        calls,
        contains('IntoChat.OS.Modules.UI.KanbanBoard.IKanbanBoard.MoveCard'),
      );
    },
  );

  test('re_highlight builds light/dark themes for all 7 languages and uiIconCatalog resolves every category', () {
    const langs = [
      'csharp',
      'dart',
      'json',
      'sql',
      'typescript',
      'yaml',
      'markdown',
    ];
    for (final lang in langs) {
      final light = buildIcCodeHighlightTheme(
        IcColors.forPreset(IcStylePreset.warmPaper, dark: false),
        lang,
      );
      final dark = buildIcCodeHighlightTheme(
        IcColors.forPreset(IcStylePreset.linearCarbon, dark: true),
        lang,
      );
      expect(light, isNotNull, reason: 'light $lang');
      expect(dark, isNotNull, reason: 'dark $lang');
      expect(light!.languages.keys, contains(lang));
      expect(dark!.languages.keys, contains(lang));
      expect(
        light.theme['keyword']!.color,
        isNot(equals(dark.theme['keyword']!.color)),
      );
    }
    expect(
      buildIcCodeHighlightTheme(IcColors.warmPaperLight, 'plaintext'),
      isNull,
    );

    expect(uiIconCategories.length, 8);
    expect(uiIconCatalog.length, greaterThanOrEqualTo(180));
    for (final cat in uiIconCategories) {
      final entries = uiIconCatalog.where((e) => e.category == cat).toList();
      expect(entries, isNotEmpty, reason: cat);
      for (final entry in entries) {
        expect(uiIcon(entry.slug), isNotNull, reason: entry.slug);
      }
    }
  });

  testWidgets(
    'CodeEditorNeuronWidget wires CodeHighlightTheme and exposes key:code-highlight:<lang> semantics',
    (tester) async {
      final handle = tester.ensureSemantics();
      Future<Map<String, dynamic>?> callNeuron(
        String contract,
        String key,
        String method,
        List<Object?> args,
      ) async {
        return {
          'version': 1,
          'title': 'Sample.cs',
          'language': 'csharp',
          'content': 'public sealed record Demo(int Count);',
          'readOnly': false,
          'wordWrap': false,
          'dirty': false,
        };
      }

      await tester.pumpWidget(
        wrap(
          SizedBox(
            width: 560,
            child: buildUiNeuronWidget(
              kind: 'ui.code-editor',
              neuronKey: 'gallery/code-editor/demo',
              callNeuron: callNeuron,
            )!,
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Sample.cs'), findsOneWidget);
      expect(find.text('CSHARP'), findsOneWidget);
      expect(
        find.bySemanticsLabel(
          RegExp(r'Syntax highlight: csharp \(\d+ spans\)'),
        ),
        findsOneWidget,
      );
      handle.dispose();
    },
  );

  testWidgets(
    'ViewHost watches IScreen ViewChanged on apps/engineer/view and reloads immediately on child Modules.UI mutation or server push with zero timers',
    (tester) async {
      var activeTab = 'plan';
      var viewLoads = 0;
      var activeViewListeners = 0;
      var activeScreenListeners = 0;
      var activeUiListeners = 0;
      final watchedTargets = <(String, String)>[];

      final viewEvents = StreamController<void>.broadcast(
        onListen: () => activeViewListeners++,
        onCancel: () => activeViewListeners--,
      );
      final screenEvents = StreamController<void>.broadcast(
        onListen: () => activeScreenListeners++,
        onCancel: () => activeScreenListeners--,
      );
      final uiEvents = StreamController<void>.broadcast(
        onListen: () => activeUiListeners++,
        onCancel: () => activeUiListeners--,
      );

      await tester.pumpWidget(
        wrap(
          ViewHost(
            knowsApp: (_) => false,
            kind: 'view',
            name: 'owner/apps/engineer/view',
            watch: (kind, name) {
              watchedTargets.add((kind, name));
              if (kind == 'screen') return screenEvents.stream;
              if (kind.startsWith('ui.')) return uiEvents.stream;
              return viewEvents.stream;
            },
            load: (_, _) async {
              viewLoads++;
              return {
                'alias': 'app.engineer',
                'generation': viewLoads,
                'view': {
                  'title': 'Engineer',
                  'acts': 'act',
                  'root': {
                    'kind': 'column',
                    'children': [
                      {'kind': 'text', 'value': 'Pane:$activeTab'},
                      {
                        'kind': 'neuronRef',
                        'refKind': 'ui.tabs',
                        'neuronUri': 'engineer/tabs',
                      },
                    ],
                  },
                },
              };
            },
            callNeuron: (contract, key, method, args) async {
              if (method == 'Select') {
                activeTab = args.first as String;
              }
              return {
                'version': viewLoads + 1,
                'items': [
                  {'id': 'plan', 'label': 'Plan'},
                  {'id': 'pr', 'label': 'PR Review'},
                ],
                'selectedId': activeTab,
                'isEnabled': true,
              };
            },
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Pane:plan'), findsOneWidget);
      expect(viewLoads, 1);
      expect(
        watchedTargets,
        contains(('screen', 'owner/app.engineer/default')),
      );
      expect(activeViewListeners, 0);
      expect(activeScreenListeners, 1);
      expect(activeUiListeners, 1);

      // Mutating child ITabs without a ViewChanged push does NOT schedule any 90ms/380ms timer.
      await tester.tap(find.text('PR Review'));
      await tester.pump(const Duration(seconds: 1));
      expect(
        viewLoads,
        1,
        reason: 'ViewHost must never poll on post-call timers',
      );
      expect(find.text('Pane:plan'), findsOneWidget);

      // Pushing ViewChanged from app.engineer reloads ViewHost immediately with zero timers.
      screenEvents.add(null);
      await tester.pump();
      await tester.pump();
      expect(viewLoads, 2);
      expect(find.text('Pane:pr'), findsOneWidget);

      // Server-initiated IScreen state change reloads ViewHost immediately too.
      activeTab = 'verify';
      screenEvents.add(null);
      await tester.pump();
      await tester.pump();
      expect(viewLoads, 3);
      expect(find.text('Pane:verify'), findsOneWidget);

      // Immediate dispose cancels the active stream subscription with zero pending timers.
      await tester.pumpWidget(const SizedBox());
      expect(activeViewListeners, 0);
      expect(activeScreenListeners, 0);
      expect(activeUiListeners, 0);

      await viewEvents.close();
      await screenEvents.close();
      await uiEvents.close();
    },
  );

  testWidgets(
    'ViewHost coalesces ViewChanged pushes arriving while initial or prior IScreen reload is in flight and cleans up on mid-reload dispose',
    (tester) async {
      var activeScreenListeners = 0;
      final watchedTargets = <(String, String)>[];
      final screenEvents = StreamController<void>.broadcast(
        onListen: () => activeScreenListeners++,
        onCancel: () => activeScreenListeners--,
      );

      final pendingLoads = <Completer<Map<String, dynamic>>>[];
      Map<String, dynamic> payload(int gen, String tab) => {
        'alias': 'app.creator',
        'generation': gen,
        'view': {
          'title': 'Creator',
          'acts': 'act',
          'root': {'kind': 'text', 'value': 'Inspector:$tab#$gen'},
        },
      };

      await tester.pumpWidget(
        wrap(
          ViewHost(
            knowsApp: (_) => false,
            kind: 'view',
            name: 'owner/app.creator/default',
            watch: (kind, name) {
              watchedTargets.add((kind, name));
              return screenEvents.stream;
            },
            load: (_, _) {
              final completer = Completer<Map<String, dynamic>>();
              pendingLoads.add(completer);
              return completer.future;
            },
          ),
        ),
      );

      // Initial _reload() #1 is in flight and already subscribed to ('screen', 'owner/app.creator/default').
      expect(pendingLoads, hasLength(1));
      expect(watchedTargets, [('screen', 'owner/app.creator/default')]);
      expect(activeScreenListeners, 1);

      // A ViewChanged push arrives while initial _reload() #1 is still in flight.
      screenEvents.add(null);
      await tester.pump();
      expect(pendingLoads, hasLength(1));

      // Once initial _reload() #1 settles, ViewHost immediately issues follow-up reload #2.
      pendingLoads[0].complete(payload(1, 'State'));
      await tester.pump();
      expect(pendingLoads, hasLength(2));

      pendingLoads[1].complete(payload(2, 'Source'));
      await tester.pumpAndSettle();
      expect(find.text('Inspector:Source#2'), findsOneWidget);

      // Now trigger reload #3 and emit two rapid ViewChanged pushes while reload #3 is in flight.
      screenEvents.add(null);
      await tester.pump();
      expect(pendingLoads, hasLength(3));

      screenEvents.add(null);
      screenEvents.add(null);
      await tester.pump();
      expect(
        pendingLoads,
        hasLength(3),
        reason: 'Overlapping pushes coalesce while reload #3 is in flight',
      );

      pendingLoads[2].complete(payload(3, 'Signals'));
      await tester.pump();
      expect(
        pendingLoads,
        hasLength(4),
        reason: 'Coalesced mid-flight push triggers one clean follow-up reload',
      );

      pendingLoads[3].complete(payload(4, 'Scenarios'));
      await tester.pumpAndSettle();
      expect(find.text('Inspector:Scenarios#4'), findsOneWidget);

      // Dispose while reload #5 is still in flight: zero leaked subscriptions or setState-after-dispose.
      screenEvents.add(null);
      await tester.pump();
      expect(pendingLoads, hasLength(5));

      await tester.pumpWidget(const SizedBox());
      expect(activeScreenListeners, 0);

      pendingLoads[4].complete(payload(5, 'Disposed'));
      await tester.pump();
      expect(pendingLoads, hasLength(5));

      await screenEvents.close();
    },
  );
}
