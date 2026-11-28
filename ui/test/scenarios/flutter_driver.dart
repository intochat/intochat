// The Flutter scenario driver: runs the scenarios exported from C#
// (`scenarios.json`, the `UiScenario` shape) on the real view host and node
// renderer, replaying the pages the kernel showed, and journals every step in
// the C# `StepTaken` shape. Its semantics mirror the kernel's reference driver
// (tests/IntoChat.OS.Tests.Unit/Scenarios/KernelDriver.cs), read from what
// Flutter draws: a target is a key path resolved through children first (the
// child's view host), then node keys (the node's keyed widget); "." is the
// page root; an expect reads the text drawn under the target; an expectShown
// holds when a widget is drawn for the target.
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

/// One step of a scenario: `act`, `expect` or `expectShown` by its `step` discriminator.
class ScenarioStep {
  ScenarioStep(this.json);

  final Map<String, dynamic> json;

  String get kind => json['step'] as String;
  String get target => json['target'] as String;
  String get signal => json['signal'] as String;
  Map<String, Object?> get args => json['args'] is Map
      ? Map<String, Object?>.from(json['args'] as Map)
      : const {};
  String get contains => json['contains'] as String;
}

class UiScenario {
  UiScenario(this.name, this.appId, this.steps);

  factory UiScenario.fromJson(Map<String, dynamic> json) =>
      UiScenario(json['name'] as String, json['appId'] as String, [
        for (final step in json['steps'] as List)
          ScenarioStep(Map<String, dynamic>.from(step as Map)),
      ]);

  final String name;
  final String appId;
  final List<ScenarioStep> steps;

  static List<UiScenario> readAll(String json) => [
    for (final item in jsonDecode(json) as List)
      UiScenario.fromJson(Map<String, dynamic>.from(item as Map)),
  ];
}

/// A page as the kernel showed it: its top-level view and the root view of
/// every child it shows, keyed by the child's key path
/// (`{"view": {...}, "children": {"nav": {"root": ...}, "a/b": ...}}`).
class PageViews {
  PageViews(this.view, this.children);

  factory PageViews.fromJson(Map<String, dynamic> json) => PageViews(
    Map<String, dynamic>.from(json['view'] as Map),
    Map<String, dynamic>.from((json['children'] as Map?) ?? const {}),
  );

  final Map<String, dynamic> view;
  final Map<String, dynamic> children;
}

/// Replays what the kernel showed for one scenario, recorded by the C#
/// `ScenarioExportFacts` at `views/<scenario>.json` as
/// `{steps: [{afterIndex, view}]}`: the page before step 0 (`afterIndex` -1)
/// and after every act that changed it. Each act must send the wire event of
/// the scenario's next act (kind, target, signal, args); the page then becomes
/// the latest one recorded at or before that act.
class ReplayHost {
  ReplayHost(this.scenario, this.recorded) {
    if (!recorded.containsKey(-1)) {
      throw StateError('${scenario.name} has no recording before step 0.');
    }
  }

  factory ReplayHost.load(UiScenario scenario, File file) {
    final json = Map<String, dynamic>.from(
      jsonDecode(file.readAsStringSync()) as Map,
    );
    return ReplayHost(scenario, {
      for (final step in json['steps'] as List)
        (step as Map)['afterIndex'] as int: PageViews.fromJson(
          Map<String, dynamic>.from(step['view'] as Map),
        ),
    });
  }

  final UiScenario scenario;
  final Map<int, PageViews> recorded;

  /// The wire events the scenario's acts sent.
  final List<Map<String, dynamic>> sent = [];

  /// The events the drawn widgets sent by themselves (a table asking for its
  /// page): the kernel answers these with views already recorded.
  final List<Map<String, dynamic>> drawn = [];

  /// True while the driver acts; any other event came from a drawn widget.
  bool acting = false;

  /// Why the last act was refused, or null.
  String? refusal;
  int _shown = -1;
  int _next = 0;

  /// How many acts have changed the page so far (the host's revision).
  int revision = 0;

  PageViews get page => recorded[_shown]!;

  /// Serves the view host: the top-level view, or a child's view.
  Future<Map<String, dynamic>> load(String kind, String name) async {
    if (name == viewName(scenario.appId)) {
      return {'generation': revision, 'view': page.view};
    }
    // A child is read by its app's key path (`{root}/children/{path}`), as
    // `GET /apps/{app}/children/{path}` serves it; the recording keys children by path.
    final prefix =
        '${viewName(scenario.appId).replaceFirst(RegExp(r'/view$'), '')}/children/';
    if (kind != 'child' || !name.startsWith(prefix)) {
      throw StateError(
        '${scenario.name} reads "$kind" "$name" outside the child route.',
      );
    }
    final child = page.children[name.substring(prefix.length)];
    if (child is Map) {
      return {'generation': revision, 'view': Map<String, dynamic>.from(child)};
    }
    throw StateError('${scenario.name} recorded no view "$name".');
  }

  /// Receives the wire event the view host's `NodeContext.act` posts.
  Future<Object?> send(Map<String, dynamic> event) async {
    if (!acting) {
      drawn.add(event);
      return null;
    }
    sent.add(event);
    refusal = null;
    while (_next < scenario.steps.length &&
        scenario.steps[_next].kind != 'act') {
      _next++;
    }
    if (_next >= scenario.steps.length) {
      throw StateError(refusal = 'no act is left in the scenario for $event');
    }
    final index = _next++;
    final step = scenario.steps[index];
    final expected = {
      'kind': 'act',
      'target': step.target,
      'signal': step.signal,
      'args': step.args,
    };
    final actual = {
      'kind': event['kind'],
      'target': event['target'],
      'signal': event['signal'],
      'args': event['args'],
    };
    if (jsonEncode(actual) != jsonEncode(expected)) {
      throw StateError(
        refusal =
            'step $index sent ${jsonEncode(actual)} but the recording expects ${jsonEncode(expected)}',
      );
    }
    final at = recorded.keys
        .where((key) => key <= index)
        .reduce((a, b) => a > b ? a : b);
    if (at != _shown) {
      _shown = at;
      revision++;
    }
    return null;
  }

  static String viewName(String appId) => 'local/apps/$appId/view';
}

/// Runs scenario steps on the real [ViewHost] and [NodeRenderer], fed by a
/// [ReplayHost]. Acts go through the root view's [NodeContext.act]; reads
/// collect what is drawn under the target's widget.
class FlutterDriver {
  FlutterDriver(this.tester, this.host);

  static const rootKey = ValueKey<String>('scenario-root');

  final WidgetTester tester;
  final ReplayHost host;
  NodeContext? _root;
  String _lastRead = '';

  String get name => 'flutter';

  /// Pumps the host for the replayed page; call once before the first step.
  Future<void> start() async {
    ViewHost.debugOnContext = (name, ctx) {
      if (name == ReplayHost.viewName(host.scenario.appId)) _root = ctx;
    };
    tester.view.physicalSize = const Size(1600, 4000);
    tester.view.devicePixelRatio = 1;
    await _pump();
  }

  void stop() {
    ViewHost.debugOnContext = null;
    tester.view.resetPhysicalSize();
    tester.view.resetDevicePixelRatio();
  }

  Future<void> _pump() async {
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: ViewHost(
            key: rootKey,
            kind: 'view',
            name: ReplayHost.viewName(host.scenario.appId),
            revision: host.revision,
            overrides: ViewHostOverrides(
              load: (kind, name) async =>
                  UiNodePayload.fromJson(kind, await host.load(kind, name)),
              onAction: (action) => host.send(action.toJson()),
              knowsApp: (_) => false,
              watch: (_, _) => const Stream.empty(),
            ),
          ),
        ),
      ),
    );
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 20));
    }
  }

  Future<void> act(ScenarioStep step) async {
    final ctx = _root ?? (throw StateError('No view is drawn to act on.'));
    host.acting = true;
    final ActivationOutcome outcome;
    try {
      outcome = await ctx.act(step.target, step.signal, step.args);
    } finally {
      host.acting = false;
    }
    if (outcome != ActivationOutcome.accepted) {
      throw StateError('The act was ${outcome.name}: ${host.refusal}.');
    }
    await _pump();
  }

  /// The text drawn under the target; throws when no widget is drawn for it.
  Future<String> read(String target) async {
    final scope = _find(target);
    if (scope == null) {
      throw StateError(
        'The ${host.scenario.appId} app draws no node or slot \'$target\'.',
      );
    }
    final texts = <String>[];
    void add(String? text) {
      if (text != null && text.isNotEmpty) texts.add(text);
    }

    final drawn = find.descendant(
      of: scope,
      matching: find.byWidgetPredicate(
        (widget) =>
            widget is RichText || widget is EditableText || widget is Semantics,
        skipOffstage: false,
      ),
      matchRoot: true,
      skipOffstage: false,
    );
    for (final widget in drawn.evaluate().map((element) => element.widget)) {
      switch (widget) {
        case RichText(:final text):
          add(text.toPlainText());
        case EditableText(:final controller):
          add(controller.text);
        case Semantics(:final properties):
          add(properties.label);
          add(properties.value);
      }
    }
    _lastRead = jsonEncode({'target': target, 'texts': texts});
    return texts.join('\n');
  }

  String? evidence(int index) => _lastRead.isEmpty ? null : _lastRead;

  // Child keys first: while the leading segment names a child drawn here,
  // descend into that child's view host; the last segment is a child, else
  // the node drawn with that key in the current view.
  Finder? _find(String target) {
    final root = find.byKey(rootKey, skipOffstage: false);
    if (target == '.') return root.evaluate().isEmpty ? null : root;
    final segments = target.split('/');
    var path = '';
    var scope = root;
    for (var i = 0; i < segments.length; i++) {
      final candidate = path.isEmpty ? segments[i] : '$path/${segments[i]}';
      final slot = find.byWidgetPredicate(
        (widget) => widget is ViewHost && widget.childPath == candidate,
        skipOffstage: false,
      );
      if (slot.evaluate().isNotEmpty) {
        if (i == segments.length - 1) return slot.first;
        path = candidate;
        scope = slot.first;
        continue;
      }
      if (i != segments.length - 1) break;
    }
    final keyed = find.descendant(
      of: scope,
      matching: find.byKey(
        ValueKey<String>(segments.last),
        skipOffstage: false,
      ),
      skipOffstage: false,
    );
    return keyed.evaluate().isEmpty ? null : keyed.first;
  }
}

/// How a run ended: the C# `RunVerdict`.
class RunVerdict {
  const RunVerdict(
    this.passed, {
    this.failedStep,
    this.reason,
    this.evidenceRef,
  });

  final bool passed;
  final int? failedStep;
  final String? reason;
  final String? evidenceRef;

  Map<String, Object?> toJson() => {
    'passed': passed,
    'failedStep': failedStep,
    'reason': reason,
    'evidenceRef': evidenceRef,
  };
}

/// A run's journal: the C# `ScenarioRunState` plus its `StepTaken` signals.
class ScenarioRun {
  ScenarioRun(this.scenario, this.driver);

  final String scenario;
  final String driver;
  final List<Map<String, Object?>> steps = [];
  RunVerdict? verdict;

  Map<String, Object?> toJson() => {
    'scenario': scenario,
    'driver': driver,
    'steps': steps.length,
    'verdict': verdict?.toJson(),
    'stepsTaken': steps,
  };

  void writeTo(Directory directory) {
    directory.createSync(recursive: true);
    File('${directory.path}/$scenario.json').writeAsStringSync(
      '${const JsonEncoder.withIndent('  ').convert(toJson())}\n',
    );
  }
}

/// Runs every step in order and stops at the first failing step, as the C#
/// `ScenarioRunner` does: outcomes are `acted`, `held` or `failed: <why>`.
Future<ScenarioRun> runScenario(
  UiScenario scenario,
  FlutterDriver driver,
) async {
  final run = ScenarioRun(scenario.name, driver.name);
  var verdict = const RunVerdict(true);
  await driver.start();
  try {
    for (var index = 0; index < scenario.steps.length; index++) {
      final step = scenario.steps[index];
      String? failure;
      String? shown;
      try {
        switch (step.kind) {
          case 'act':
            await driver.act(step);
          case 'expect':
            final text = await driver.read(step.target);
            shown = _clip(text);
            if (!text.contains(step.contains)) {
              failure =
                  '${step.target}: expected "${step.contains}" but it shows "$shown"';
            }
          case 'expectShown':
            try {
              shown = _clip(await driver.read(step.target));
            } catch (error) {
              failure =
                  '${step.target}: expected it to be shown but the ${driver.name} driver found none: $error';
            }
          default:
            failure = '${step.target}: unknown step ${step.kind}';
        }
      } catch (error) {
        failure = '${step.target}: the ${driver.name} driver failed: $error';
      }
      final evidence = driver.evidence(index);
      run.steps.add({
        'index': index,
        'step': step.json,
        'outcome': failure == null
            ? (step.kind == 'act' ? 'acted' : 'held')
            : 'failed: $failure',
        'shown': shown,
        'evidenceRef': evidence,
      });
      if (failure != null) {
        verdict = RunVerdict(
          false,
          failedStep: index,
          reason: failure,
          evidenceRef: evidence,
        );
        break;
      }
    }
  } finally {
    driver.stop();
  }
  run.verdict = verdict;
  return run;
}

String _clip(String text) =>
    text.length <= 200 ? text : '${text.substring(0, 200)}...';
