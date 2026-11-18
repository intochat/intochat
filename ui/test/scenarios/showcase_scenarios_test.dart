import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'flutter_driver.dart';

// Runs the showcase scenarios exported from C# on the Flutter driver: the real
// view host and node renderer draw the pages the kernel showed (views/<scenario>.json, recorded by
// ScenarioExportFacts), and writes each run to build/scenario-runs/<name>.json.
// Every scenario must have a recording: a missing one fails, never skips.
void main() {
  final scenarios = UiScenario.readAll(
    File('test/scenarios/scenarios.json').readAsStringSync(),
  );
  const views = 'test/scenarios/views';
  final runs = Directory('build/scenario-runs');

  test('scenarios.json holds the showcase scenarios', () {
    expect(scenarios, isNotEmpty);
    for (final scenario in scenarios) {
      expect(scenario.appId, 'uikit');
      expect(scenario.steps.first.kind, 'act');
      for (final step in scenario.steps) {
        expect(const {'act', 'expect', 'expectShown'}, contains(step.kind));
        if (step.kind == 'expect') expect(step.contains, isNotEmpty);
      }
    }
  });

  test('every scenario has a recording and every recording a scenario', () {
    final recorded = {
      for (final file in Directory(views).listSync().whereType<File>())
        file.uri.pathSegments.last.replaceAll('.json', ''),
    };
    expect(recorded, {for (final scenario in scenarios) scenario.name});
  });

  for (final scenario in scenarios) {
    testWidgets('showcase scenario ${scenario.name} passes on Flutter', (
      tester,
    ) async {
      final file = File('$views/${scenario.name}.json');
      expect(
        file.existsSync(),
        isTrue,
        reason:
            '${file.path} is missing; run ScenarioExportFacts with '
            'INTOCHAT_REGENERATE_SCENARIOS=1.',
      );
      final host = ReplayHost.load(scenario, file);
      final run = await runScenario(scenario, FlutterDriver(tester, host));
      run.writeTo(runs);
      expect(run.verdict!.passed, isTrue, reason: run.verdict!.reason);
      expect(
        host.sent,
        hasLength(scenario.steps.where((step) => step.kind == 'act').length),
      );
    });
  }

  testWidgets('a scenario fails when its node kind draws nothing', (
    tester,
  ) async {
    final scenario = scenarios.singleWhere((item) => item.name == 'live-chart');
    final host = ReplayHost.load(scenario, File('$views/live-chart.json'));
    NodeRenderer.debugReplace = (node) =>
        node is LiveChartViewNode ? const SizedBox.shrink() : null;
    try {
      final run = await runScenario(scenario, FlutterDriver(tester, host));
      expect(run.verdict!.passed, isFalse);
      expect(
        run.verdict!.reason,
        contains('live-chart: expected "Live points"'),
      );
    } finally {
      NodeRenderer.debugReplace = null;
    }
  });

  testWidgets('a refused act names the mismatch', (tester) async {
    final scenario = scenarios.singleWhere((item) => item.name == 'checkBox');
    final wrong = UiScenario(scenario.name, scenario.appId, [
      ScenarioStep({
        ...scenario.steps.first.json,
        'args': {'id': 'other'},
      }),
    ]);
    final host = ReplayHost(
      scenario,
      ReplayHost.load(scenario, File('$views/checkBox.json')).recorded,
    );

    final run = await runScenario(wrong, FlutterDriver(tester, host));

    expect(run.verdict!.failedStep, 0);
    expect(run.verdict!.reason, contains('"args":{"id":"other"}'));
    expect(run.verdict!.reason, contains('"args":{"id":"checkBox"}'));
  });

  testWidgets('a target is not shown before the act that opens it', (
    tester,
  ) async {
    final scenario = scenarios.singleWhere((item) => item.name == 'checkBox');
    final early = UiScenario(scenario.name, scenario.appId, [
      ScenarioStep({'step': 'expectShown', 'target': 'checkBox'}),
    ]);
    final host = ReplayHost(
      scenario,
      ReplayHost.load(scenario, File('$views/checkBox.json')).recorded,
    );

    final run = await runScenario(early, FlutterDriver(tester, host));

    expect(run.verdict!.failedStep, 0);
  });
}
