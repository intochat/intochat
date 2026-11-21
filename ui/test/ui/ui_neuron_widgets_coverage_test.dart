import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  Widget wrap(Widget child) => MaterialApp(
    theme: UiTheme.light(null),
    home: Scaffold(
      body: SingleChildScrollView(
        child: Padding(padding: const EdgeInsets.all(16), child: child),
      ),
    ),
  );

  // Covers all 20 UI neuron widgets that previously had zero widget tests.
  final sampleStates = <String, Map<String, dynamic>>{
    'ui.checkbox': {
      'label': 'Accept terms',
      'isChecked': true,
      'isEnabled': true,
    },
    'ui.select': {
      'label': 'Region',
      'selectedValue': 'eu',
      'options': [
        {'value': 'us', 'label': 'US East'},
        {'value': 'eu', 'label': 'EU West'},
      ],
    },
    'ui.tabs': {
      'activeTabId': 'overview',
      'tabs': [
        {'id': 'overview', 'label': 'Overview'},
        {'id': 'logs', 'label': 'Logs'},
      ],
    },
    'ui.expander': {
      'header': 'Advanced Settings',
      'summary': 'Click to expand',
      'isExpanded': true,
    },
    'ui.listbox': {
      'label': 'Items',
      'items': [
        {'id': 'a', 'label': 'Alpha'},
        {'id': 'b', 'label': 'Beta'},
      ],
      'selectedIds': ['a'],
    },
    'ui.table': {
      'title': 'Deployments',
      'columns': [
        {'key': 'name', 'label': 'Name'},
        {'key': 'status', 'label': 'Status'},
      ],
      'rows': [
        {
          'id': 'r1',
          'cells': {'name': 'kernel', 'status': 'healthy'},
        },
      ],
    },
    'ui.form': {
      'title': 'Profile',
      'submitLabel': 'Save',
      'fields': [
        {'name': 'email', 'label': 'Email', 'value': 'dev@intochat.io'},
      ],
    },
    'ui.dialog': {
      'title': 'Confirm Delete',
      'message': 'Delete selected workspace?',
      'isOpen': true,
      'primaryLabel': 'Delete',
      'secondaryLabel': 'Cancel',
    },
    'ui.slider': {
      'label': 'Volume',
      'min': 0.0,
      'max': 100.0,
      'step': 5.0,
      'value': 40.0,
      'unit': '%',
    },
    'ui.radio-buttons': {
      'label': 'Execution Mode',
      'selectedValue': 'wasm-ui',
      'options': [
        {'value': 'wasm-ui', 'label': 'WASM Web UI'},
        {'value': 'mcp', 'label': 'MCP Server'},
      ],
    },
    'ui.rating': {'label': 'Quality', 'value': 4.0, 'max': 5},
    'ui.color-picker': {'label': 'Accent', 'hex': '#60A5FA'},
    'ui.breadcrumb': {
      'items': [
        {'id': 'root', 'label': 'Home'},
        {'id': 'ui', 'label': 'UI Kit'},
      ],
    },
    'ui.auto-suggest': {
      'label': 'Search',
      'query': 'tog',
      'suggestions': ['Toggle', 'ToggleSplitButton'],
    },
    'ui.progress': {
      'label': 'Building WASM',
      'value': 0.65,
      'isIndeterminate': false,
    },
    'ui.info-bar': {
      'title': 'Ready',
      'message': 'All neurons online',
      'severity': 'success',
      'isOpen': true,
    },
    'ui.date-picker': {'label': 'Release Date', 'isoDate': '2026-11-20'},
    'ui.time-picker': {'label': 'Reminder Time', 'hour': 9, 'minute': 30},
    'ui.context-menu': {
      'title': 'Actions',
      'isOpen': true,
      'items': [
        {'id': 'copy', 'label': 'Copy'},
        {'id': 'delete', 'label': 'Delete'},
      ],
    },
    'ui.markdown': {
      'title': 'Notes',
      'markdown': '# Hello\n**Widgetbook** is live.',
    },
  };

  for (final entry in sampleStates.entries) {
    testWidgets('${entry.key} renders cleanly via buildUiNeuronWidget', (
      tester,
    ) async {
      final called = <String>[];
      final widget = buildUiNeuronWidget(
        kind: entry.key,
        neuronKey: 'coverage/${entry.key}',
        callNeuron: (contract, key, method, args) async {
          called.add(method);
          return entry.value;
        },
      );
      expect(widget, isNotNull, reason: '${entry.key} must be registered');

      await tester.pumpWidget(wrap(widget!));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));

      expect(called, contains('Read'));
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets(
    'SliderWidget commits SetValue only on drag end (no per-tick RPC spam)',
    (tester) async {
      final setCalls = <double>[];
      var current = 20.0;

      final widget = buildUiNeuronWidget(
        kind: 'ui.slider',
        neuronKey: 'coverage/slider-drag',
        callNeuron: (contract, key, method, args) async {
          if (method == 'SetValue' && args.isNotEmpty) {
            current = (args.first as num).toDouble();
            setCalls.add(current);
          }
          return {
            'label': 'Gain',
            'min': 0.0,
            'max': 100.0,
            'step': 1.0,
            'value': current,
            'unit': '%',
            'isEnabled': true,
          };
        },
      )!;

      await tester.pumpWidget(wrap(widget));
      await tester.pumpAndSettle();

      final sliderFinder = find.byType(Slider);
      expect(sliderFinder, findsOneWidget);

      await tester.drag(sliderFinder, const Offset(120, 0));
      await tester.pumpAndSettle();

      // Must commit exactly once on drag end rather than firing on every drag tick.
      expect(setCalls.length, equals(1));
    },
  );

  testWidgets(
    'NeuronWidget surfaces watch stream errors instead of swallowing them',
    (tester) async {
      final watchCtrl = StreamController<void>.broadcast();
      addTearDown(watchCtrl.close);

      final widget = NeuronWidget(
        kind: 'ui.toggle',
        neuronKey: 'coverage/error-watch',
        callNeuron: (_, _, _, _) async => throw StateError('offline'),
        watch: (_, _) => watchCtrl.stream,
        builder: (_, _, _) => const Text('ok'),
      );

      await tester.pumpWidget(wrap(widget));
      await tester.pumpAndSettle();

      expect(find.textContaining('offline'), findsOneWidget);
      expect(find.text('Retry'), findsOneWidget);
    },
  );
}
