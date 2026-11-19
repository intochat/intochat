import 'dart:async';
import 'dart:math' as math;

import 'package:flame/components.dart';
import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../flame/prefabs/signal_pulse.dart';
import '../flame/scene_game.dart';
import '../flame/scene_view.dart';
import '../theme/wire_node_values.dart';
import 'node_common.dart';
import 'node_context.dart';

/// The journal tail a brain scene draws as pulses; the Creator provides it above its view, and a scene without one stays still.
class BrainSignals extends InheritedWidget {
  const BrainSignals({super.key, required this.signals, required super.child});

  final Stream<List<JournalTailSignal>> signals;

  static Stream<List<JournalTailSignal>>? of(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<BrainSignals>()?.signals;

  @override
  bool updateShouldNotify(BrainSignals old) => old.signals != signals;
}

const _tints = {
  'unit': '#4FC3F7',
  'app': '#81C784',
  'initial': '#34D399',
  'state': '#60A5FA',
  'kernel': '#9E9E9E',
  'draft': '#CE93D8',
};

/// A `*` edge (any publisher) is drawn as a stub this far above its target.
const _stub = 90.0;

/// The key of the view's button that selects an edge or a pulse: they are not nodes of the view, so the scene activates
/// this key with theirs as the value (BrainGraph.SelectKey).
const brainSelectKey = 'graph:select';

/// A synapse labelled `replay:<index>:<seq>:<alias>:<traceId>` is a step of the open trace's replay, not an edge: from the
/// publisher towards the node the alias reaches, active on the current step.
const brainReplayPrefix = 'replay:';

/// The ordered replay steps the view carries, and the active one (null while live).
({List<ReplayStep> steps, int? at}) brainReplay(BrainSceneNode node) {
  final steps = <ReplayStep>[];
  int? at;
  for (final edge in node.synapses) {
    final parsed = parseBrainReplayLabel(edge.signalLabel, brainReplayPrefix);
    if (parsed == null) continue;
    steps.add(
      ReplayStep(
        index: parsed.index,
        seq: parsed.seq,
        alias: parsed.alias,
        from: edge.fromId,
        to: edge.toId,
        traceId: parsed.traceId,
      ),
    );
    if (edge.active) at = steps.length - 1;
  }
  return (steps: steps, at: at);
}

bool _isReplay(BrainGraphSynapse edge) =>
    edge.signalLabel?.startsWith(brainReplayPrefix) ?? false;

/// The inspector's key for an edge: `edge:<from>→<to>:<alias>`.
String brainEdgeKey(BrainGraphSynapse edge) =>
    'edge:${edge.fromId}→${edge.toId}:${edge.signalLabel ?? ''}';

/// The drawable edges of [node]: those whose target is a drawn node, from the source node or, for `*`, from a stub above it.
List<PulseEdge> brainEdges(BrainSceneNode node) {
  final at = {for (final n in node.nodes) n.id: Vector2(n.x, n.y)};
  return [
    for (final edge in node.synapses)
      if (!_isReplay(edge))
        if (at[edge.toId] case final to?)
          if (isWildcardEdgeOrigin(edge.fromId) || at.containsKey(edge.fromId))
            PulseEdge(
              key: brainEdgeKey(edge),
              fromId: edge.fromId,
              toId: edge.toId,
              alias: stripSignalAliasHash(edge.signalLabel),
              start: isWildcardEdgeOrigin(edge.fromId)
                  ? to + Vector2(0, -_stub)
                  : at[edge.fromId]!.clone(),
              end: to.clone(),
            ),
  ];
}

/// The scene of [node]: a sphere per node, kind-tinted (a draft is a half-transparent ghost), a line per edge.
SceneState brainSceneState(BrainSceneNode node, {int revision = 1}) {
  final nodes = <SceneNode>[];
  for (final edge in brainEdges(node)) {
    final isSelfLoop =
        (edge.start.x - edge.end.x).abs() < 0.01 &&
        (edge.start.y - edge.end.y).abs() < 0.01;
    final left = math.min(edge.start.x, edge.end.x);
    final top = math.min(edge.start.y, edge.end.y);
    final w = (edge.start.x - edge.end.x).abs(),
        h = (edge.start.y - edge.end.y).abs();
    final pathD = isSelfLoop
        ? 'M 0 0 C -26 -38 26 -38 4 0'
        : 'M ${edge.start.x - left} ${edge.start.y - top} L ${edge.end.x - left} ${edge.end.y - top}';
    nodes.add(
      SceneNode(
        id: edge.key,
        type: 'path',
        transform: SceneTransform(
          x: isSelfLoop ? edge.start.x : left + w / 2,
          y: isSelfLoop ? edge.start.y - 18 : top + h / 2,
        ),
        style: isSelfLoop ? '#B3D97706' : '#808B9BB4',
        parameters: {'d': pathD, 'stroke': '2', 'alias': edge.alias},
        inputs: const ['tap'],
      ),
    );
  }
  for (final n in node.nodes) {
    nodes.add(
      SceneNode(
        id: n.id,
        type: 'sphere',
        transform: SceneTransform(
          x: n.x,
          y: n.y,
          opacity: isDraftBrainNode(n.kind) ? .5 : 1,
        ),
        style: _tints[n.kind] ?? _tints['kernel'],
        parameters: {
          'title': n.label,
          'description': n.subtitle ?? n.kind,
          'kind': n.kind,
        },
        inputs: const ['tap'],
      ),
    );
  }
  return SceneState(
    name: 'brain',
    revision: revision,
    definition: SceneDefinition(
      nodes: nodes,
      camera: const SceneCamera(zoom: .5),
    ),
  );
}

/// The brain as a graph with live pulses.
Widget renderBrainScene(BrainSceneNode node, NodeContext ctx) => SizedBox(
  height: 420,
  child: BrainSceneView(node: node, ctx: ctx),
);

class BrainSceneView extends StatefulWidget {
  const BrainSceneView({
    super.key,
    required this.node,
    required this.ctx,
    this.signals,
  });

  final BrainSceneNode node;
  final NodeContext ctx;

  /// The tail to draw; [BrainSignals] above when null.
  final Stream<List<JournalTailSignal>>? signals;

  @override
  State<BrainSceneView> createState() => BrainSceneViewState();
}

class BrainSceneViewState extends State<BrainSceneView> {
  final game = SceneGame();
  final pulses = SignalPulses();
  late final _clock = SignalPulseClock(pulses);
  late SceneState _state;
  int _revision = 1;
  String _structure = '';
  var _nodes = <String, BrainGraphNode>{};

  /// How many times the scene was laid out.
  @visibleForTesting
  int layouts = 0;
  StreamSubscription<List<JournalTailSignal>>? _tail;
  Stream<List<JournalTailSignal>>? _source;

  @override
  void initState() {
    super.initState();
    _layout();
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _listen(widget.signals ?? BrainSignals.of(context));
    _replay();
  }

  @override
  void didUpdateWidget(BrainSceneView oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (_structureOf(widget.node) != _structure) {
      _revision++;
      _layout();
    }
    _listen(widget.signals ?? BrainSignals.of(context));
    _replay();
  }

  /// Plays the current replay step as pulses; live pulses pause while one is active and resume when it ends.
  void _replay() {
    final replay = brainReplay(widget.node);
    _spawn(pulses.replay(replay.steps, replay.at));
  }

  static String _structureOf(BrainSceneNode node) => [
    for (final n in node.nodes)
      '${n.id}|${n.kind}|${n.label}|${n.subtitle}|${n.x}|${n.y}|${n.select?.key}',
    for (final e in node.synapses)
      if (!_isReplay(e)) '${e.fromId}|${e.toId}|${e.signalLabel}',
  ].join(';');

  void _layout() {
    layouts++;
    _structure = _structureOf(widget.node);
    _nodes = {for (final n in widget.node.nodes) n.id: n};
    game.world.children.whereType<SignalPulseComponent>().toList().forEach(
      (c) => c.removeFromParent(),
    );
    _state = brainSceneState(widget.node, revision: _revision);
    pulses.layout(brainEdges(widget.node), {
      for (final n in widget.node.nodes) n.id: Vector2(n.x, n.y),
    });
  }

  void _listen(Stream<List<JournalTailSignal>>? source) {
    if (identical(source, _source)) return;
    _source = source;
    unawaited(_tail?.cancel());
    _tail = source?.listen(_feed);
  }

  void _feed(List<JournalTailSignal> batch) {
    if (!mounted) return;
    _spawn(pulses.feed(batch));
  }

  void _spawn(List<SignalPulse> added) {
    if (added.isEmpty) return;
    if (_clock.parent == null) game.world.add(_clock);
    for (final pulse in added) {
      game.world.add(
        SignalPulseComponent(
          pulse,
          onTap: (key) =>
              unawaited(widget.ctx.activate(brainSelectKey, value: key)),
          onArrived: pulses.arrived,
        ),
      );
    }
    game.wake();
  }

  void _input(String id, String kind, double x, double y, String? payload) {
    if (!isTapBrainInput(kind)) return;
    if (id.startsWith('edge:')) {
      unawaited(widget.ctx.activate(brainSelectKey, value: id));
      return;
    }
    pressOf(_nodes[id]?.select, widget.ctx)?.call();
  }

  @override
  void dispose() {
    unawaited(_tail?.cancel());
    _tail = null;
    pulses.clear();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) =>
      SceneView(sceneKey: 'brain', state: _state, game: game, onInput: _input);
}
