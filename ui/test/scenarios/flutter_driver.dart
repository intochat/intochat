// The Flutter scenario driver: runs the scenarios exported from C#
// (`scenarios.json`, the `UiScenario` shape) against recorded page views and
// journals every step in the C# `StepTaken` shape. Its semantics mirror the
// kernel's reference driver (tests/IntoChat.OS.Tests.Unit/Scenarios/KernelDriver.cs):
// a target is a key path resolved through slots first, then node keys; "." is
// the page root; an expect reads the visible text under the target; an
// expectShown holds when the target exists.
import 'dart:convert';
import 'dart:io';

import 'package:intochat_os_flutter_shell/src/nodes/node_context.dart';

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
/// every slot child it shows, keyed by the child's `neuronUri`
/// (`{"view": {...}, "slots": {uri: {"root": ...}}}`).
class PageViews {
  PageViews(this.view, this.slots);

  factory PageViews.fromJson(Map<String, dynamic> json) => PageViews(
    json['view'],
    Map<String, dynamic>.from((json['slots'] as Map?) ?? const {}),
  );

  final Object? view;
  final Map<String, dynamic> slots;
}

/// The surface a driver acts on: what a page shows and where acts go.
abstract class ScenarioHost {
  /// The top-level view the app shows now; null before any page is shown.
  Object? get view;

  /// The root view of a slot child, or null when the host has none.
  Object? childView(String neuronUri);

  /// Receives the wire event a [NodeContext.act] posts.
  Future<Object?> send(Map<String, dynamic> event);
}

/// Replays what the kernel showed for one scenario, recorded by the C#
/// `ScenarioExportFacts` at `views/<scenario>.json` as
/// `{steps: [{afterIndex, view}]}`: the page before step 0 (`afterIndex` -1)
/// and after every act. Each act must send the wire event of the scenario's
/// next act (target, signal, args); the page then becomes the one recorded
/// after that act.
class ReplayHost implements ScenarioHost {
  ReplayHost(this.scenario, this.recorded) : _page = recorded[-1] {
    if (_page == null) {
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
  final List<Map<String, dynamic>> sent = [];
  PageViews? _page;
  int _next = 0;

  @override
  Object? get view => _page?.view;

  @override
  Object? childView(String neuronUri) => _page?.slots[neuronUri];

  @override
  Future<Object?> send(Map<String, dynamic> event) async {
    sent.add(event);
    while (_next < scenario.steps.length &&
        scenario.steps[_next].kind != 'act') {
      _next++;
    }
    if (_next >= scenario.steps.length) {
      throw StateError('${scenario.name} has no act left for $event.');
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
      throw StateError('Step $index sent $actual, expected $expected.');
    }
    _page =
        recorded[index] ??
        (throw StateError(
          '${scenario.name} has no recording after step $index.',
        ));
    return null;
  }
}

/// Runs scenario steps against a [ScenarioHost], acting through
/// [NodeContext.act] (the same `{kind:"act", neuron, target, signal, args}`
/// event the client posts to `/brains/{brain}/act`).
class FlutterDriver {
  FlutterDriver(this.host, {required this.appId});

  static const maxSlotDepth = 4;

  final ScenarioHost host;
  final String appId;
  String _lastRead = '';

  String get name => 'flutter';

  NodeContext get _context => NodeContext(
    view: 'local/apps/$appId/view',
    send: host.send,
    actNeuron: 'local/apps/$appId',
  );

  Future<void> act(ScenarioStep step) async {
    final outcome = await _context.act(step.target, step.signal, step.args);
    if (outcome != ActivationOutcome.accepted) {
      throw StateError('The act was ${outcome.name}.');
    }
  }

  /// The text the target shows; throws when the page shows no node or slot there.
  String read(String target) {
    final view =
        host.view ?? (throw StateError('The $appId app shows no view.'));
    final subtree = target == '.'
        ? view
        : _find(view, target) ??
              (throw StateError(
                'The $appId app shows no node or slot \'$target\'.',
              ));
    final texts = <String>[];
    _collect(subtree, texts, 0);
    _lastRead = jsonEncode({'target': target, 'texts': texts});
    return texts.join('\n');
  }

  String? evidence(int index) => _lastRead.isEmpty ? null : _lastRead;

  Object? _find(Object? view, String target) {
    var current = view;
    var rest = target;
    for (var depth = 0; depth < maxSlotDepth; depth++) {
      final slash = rest.indexOf('/');
      if (slash <= 0) break;
      final slot = _slot(current, rest.substring(0, slash));
      final child = slot == null ? null : _childView(slot.$2);
      if (child == null) break;
      current = child;
      rest = rest.substring(slash + 1);
    }
    return _slot(current, rest)?.$1 ?? _keyed(current, rest);
  }

  Object? _childView(String uri) {
    final view = host.childView(uri);
    return view is Map && view.containsKey('root') ? view['root'] : null;
  }

  static (Object, String)? _slot(Object? json, String key) {
    if (json is Map) {
      final slot = _slotOf(json);
      if (slot != null) return slot.$1 == key ? (json, slot.$2) : null;
      for (final entry in json.entries) {
        if (_isData(entry.key as String)) continue;
        final found = _slot(entry.value, key);
        if (found != null) return found;
      }
    } else if (json is List) {
      for (final item in json) {
        final found = _slot(item, key);
        if (found != null) return found;
      }
    }
    return null;
  }

  static Object? _keyed(Object? json, String key) {
    if (json is Map) {
      if (_slotOf(json) != null) return null;
      if (json['key'] == key) return json;
      for (final entry in json.entries) {
        if (_isData(entry.key as String)) continue;
        final found = _keyed(entry.value, key);
        if (found != null) return found;
      }
    } else if (json is List) {
      for (final item in json) {
        final found = _keyed(item, key);
        if (found != null) return found;
      }
    }
    return null;
  }

  static const _hidden = {
    'key',
    'kind',
    'refKind',
    'neuronUri',
    'icon',
    'tone',
    'id',
    'automationName',
  };

  void _collect(Object? json, List<String> texts, int depth) {
    if (json is Map) {
      final slot = _slotOf(json);
      if (slot != null) {
        if (json['props'] != null) _collect(json['props'], texts, depth);
        if (depth < maxSlotDepth) {
          final child = _childView(slot.$2);
          if (child != null) _collect(child, texts, depth + 1);
        }
        return;
      }
      for (final entry in json.entries) {
        final name = entry.key as String;
        if (_isData(name) || _hidden.contains(name)) continue;
        _collect(entry.value, texts, depth);
      }
    } else if (json is List) {
      for (final item in json) {
        _collect(item, texts, depth);
      }
    } else if (json is String && json.isNotEmpty) {
      texts.add(json);
    }
  }

  static bool _isData(String name) => const {
    'command',
    'emit',
    'args',
    'intent',
    'resolve',
    'onSubmit',
    'props',
  }.contains(name);

  // (slot key, child uri) when this node is a slot: refKind "slot" and a
  // neuronUri "brain/kind/key" whose key has a '/'; the slot key is its last segment.
  static (String, String)? _slotOf(Map json) {
    final uri = json['neuronUri'];
    if (json['refKind'] != 'slot' || uri is! String) return null;
    final parts = uri.split('/');
    if (parts.length < 3) return null;
    final key = parts.skip(2).join('/');
    final slash = key.lastIndexOf('/');
    return slash > 0 ? (key.substring(slash + 1), uri) : null;
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
  for (var index = 0; index < scenario.steps.length; index++) {
    final step = scenario.steps[index];
    String? failure;
    String? shown;
    try {
      switch (step.kind) {
        case 'act':
          await driver.act(step);
        case 'expect':
          final text = driver.read(step.target);
          shown = _clip(text);
          if (!text.contains(step.contains)) {
            failure =
                '${step.target}: expected "${step.contains}" but it shows "$shown"';
          }
        case 'expectShown':
          try {
            shown = _clip(driver.read(step.target));
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
  run.verdict = verdict;
  return run;
}

String _clip(String text) =>
    text.length <= 200 ? text : '${text.substring(0, 200)}...';
