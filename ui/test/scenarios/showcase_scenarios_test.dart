import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

import 'flutter_driver.dart';

// Runs the showcase scenarios exported from C# on the Flutter driver, replaying
// the pages the kernel showed (views/<scenario>.json, recorded by
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
    test('showcase scenario ${scenario.name} passes on Flutter', () async {
      final file = File('$views/${scenario.name}.json');
      expect(
        file.existsSync(),
        isTrue,
        reason:
            '${file.path} is missing; run ScenarioExportFacts with '
            'INTOCHAT_REGENERATE_SCENARIOS=1.',
      );
      final host = ReplayHost.load(scenario, file);
      final run = await runScenario(
        scenario,
        FlutterDriver(host, appId: 'uikit'),
      );
      run.writeTo(runs);
      expect(run.verdict!.passed, isTrue, reason: run.verdict!.reason);
      expect(
        host.sent,
        hasLength(scenario.steps.where((step) => step.kind == 'act').length),
      );
    });
  }

  group('driver', () {
    final blank = PageViews.fromJson({
      'view': {
        'root': {'kind': 'column', 'children': <Object>[]},
      },
    });
    final demo = PageViews.fromJson({
      'view': {
        'root': {
          'kind': 'column',
          'children': [
            {'kind': 'text', 'key': 'title', 'text': 'Demo page'},
            {
              'kind': 'neuronRef',
              'refKind': 'slot',
              'neuronUri': 'local/control.checkBox/uikit/remember',
              'props': {'label': 'Remember me'},
            },
          ],
        },
      },
      'slots': {
        'local/control.checkBox/uikit/remember': {
          'root': {
            'kind': 'row',
            'children': [
              {'kind': 'text', 'key': 'state', 'text': 'Off'},
            ],
          },
        },
      },
    });

    UiScenario scenario(List<Map<String, dynamic>> steps) => UiScenario(
      'demo',
      'uikit',
      [for (final step in steps) ScenarioStep(step)],
    );

    final select = {
      'step': 'act',
      'target': 'nav',
      'signal': 'control.select',
      'args': {'id': 'demo'},
    };

    Future<(ScenarioRun, ReplayHost)> run(
      List<Map<String, dynamic>> steps,
    ) async {
      final s = scenario(steps);
      final act = steps.indexWhere((step) => step['step'] == 'act');
      final host = ReplayHost(s, {-1: blank, act: demo});
      return (await runScenario(s, FlutterDriver(host, appId: 'uikit')), host);
    }

    test('replays the page recorded after each act', () async {
      final (result, host) = await run([
        {'step': 'expectShown', 'target': '.'},
        select,
        {'step': 'expect', 'target': '.', 'contains': 'Demo page'},
        {'step': 'expect', 'target': 'remember', 'contains': 'Remember me'},
        {'step': 'expect', 'target': 'remember/state', 'contains': 'Off'},
        {'step': 'expectShown', 'target': 'title'},
      ]);

      expect(result.verdict!.passed, isTrue, reason: result.verdict!.reason);
      expect(host.sent.single, {
        'kind': 'act',
        'neuron': 'local/apps/uikit',
        'target': 'nav',
        'signal': 'control.select',
        'args': {'id': 'demo'},
      });
      expect(result.steps.map((step) => step['outcome']), [
        'held',
        'acted',
        'held',
        'held',
        'held',
        'held',
      ]);
      expect(result.steps[2]['shown'], contains('Demo page'));
    });

    test('a target shown only after the act is not shown before it', () async {
      final (result, _) = await run([
        {'step': 'expectShown', 'target': 'title'},
        select,
      ]);

      expect(result.verdict!.failedStep, 0);
    });

    test('fails an act whose wire event differs from the recording', () async {
      final s = scenario([select]);
      final host = ReplayHost(s, {-1: blank, 0: demo});
      final driver = FlutterDriver(host, appId: 'uikit');

      final result = await runScenario(
        UiScenario('demo', 'uikit', [
          ScenarioStep({
            ...select,
            'args': {'id': 'other'},
          }),
        ]),
        driver,
      );

      expect(result.verdict!.failedStep, 0);
      expect(result.verdict!.reason, contains('act was refused'));
    });

    test('fails at the first step that does not hold', () async {
      final (result, _) = await run([
        select,
        {'step': 'expectShown', 'target': 'missing'},
        {'step': 'expect', 'target': '.', 'contains': 'Demo page'},
      ]);

      expect(result.verdict!.passed, isFalse);
      expect(result.verdict!.failedStep, 1);
      expect(result.steps, hasLength(2));
      expect(result.steps[1]['outcome'], startsWith('failed: missing:'));
    });

    test('fails an expect whose text is not shown', () async {
      final (result, _) = await run([
        select,
        {'step': 'expect', 'target': 'remember', 'contains': 'On'},
      ]);

      expect(result.verdict!.failedStep, 1);
      expect(result.verdict!.reason, contains('expected "On"'));
    });
  });
}
