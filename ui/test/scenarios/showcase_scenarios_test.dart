import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

import 'flutter_driver.dart';

// Runs the showcase scenarios exported from C# on the Flutter driver against
// the recorded UI Kit pages under views/uikit, and writes each run to
// build/scenario-runs/<name>.json. A scenario whose page is not recorded is
// skipped with that reason; the driver's own semantics are proven on an
// inline page below.
void main() {
  final scenarios = UiScenario.readAll(
    File('test/scenarios/scenarios.json').readAsStringSync(),
  );
  final pages = Directory('test/scenarios/views/uikit');
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

  for (final scenario in scenarios) {
    final page = File('${pages.path}/${scenario.name}.json');
    test('showcase scenario ${scenario.name} passes on Flutter', () async {
      final driver = FlutterDriver(
        RecordedPagesHost.load(pages),
        appId: 'uikit',
      );
      final run = await runScenario(scenario, driver);
      run.writeTo(runs);
      expect(run.verdict!.passed, isTrue, reason: run.verdict!.reason);
    }, skip: page.existsSync() ? false : 'No recorded page ${page.path}.');
  }

  group('driver', () {
    final host = RecordedPagesHost({
      'demo': PageViews.fromJson({
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
      }),
    });
    final driver = FlutterDriver(host, appId: 'uikit');

    UiScenario scenario(List<Map<String, dynamic>> steps) => UiScenario(
      'demo',
      'uikit',
      [for (final step in steps) ScenarioStep(step)],
    );

    test('acts through the act wire event and reads through slots', () async {
      final run = await runScenario(
        scenario([
          {
            'step': 'act',
            'target': 'nav',
            'signal': 'control.select',
            'args': {'id': 'demo'},
          },
          {'step': 'expect', 'target': '.', 'contains': 'Demo page'},
          {'step': 'expect', 'target': 'remember', 'contains': 'Remember me'},
          {'step': 'expect', 'target': 'remember/state', 'contains': 'Off'},
          {'step': 'expectShown', 'target': 'title'},
        ]),
        driver,
      );

      expect(run.verdict!.passed, isTrue, reason: run.verdict!.reason);
      expect(host.sent.single, {
        'kind': 'act',
        'neuron': 'local/apps/uikit',
        'target': 'nav',
        'signal': 'control.select',
        'args': {'id': 'demo'},
      });
      expect(run.steps.map((step) => step['outcome']), [
        'acted',
        'held',
        'held',
        'held',
        'held',
      ]);
      expect(run.steps[1]['shown'], contains('Demo page'));
    });

    test('fails at the first step that does not hold', () async {
      final run = await runScenario(
        scenario([
          {
            'step': 'act',
            'target': 'nav',
            'signal': 'control.select',
            'args': {'id': 'demo'},
          },
          {'step': 'expectShown', 'target': 'missing'},
          {'step': 'expect', 'target': '.', 'contains': 'Demo page'},
        ]),
        driver,
      );

      expect(run.verdict!.passed, isFalse);
      expect(run.verdict!.failedStep, 1);
      expect(run.steps, hasLength(2));
      expect(run.steps[1]['outcome'], startsWith('failed: missing:'));
    });

    test('fails an expect whose text is not shown', () async {
      final run = await runScenario(
        scenario([
          {
            'step': 'act',
            'target': 'nav',
            'signal': 'control.select',
            'args': {'id': 'demo'},
          },
          {'step': 'expect', 'target': 'remember', 'contains': 'On'},
        ]),
        driver,
      );

      expect(run.verdict!.failedStep, 1);
      expect(run.verdict!.reason, contains('expected "On"'));
    });
  });
}
