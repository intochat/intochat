import 'dart:async';

import 'package:flame/components.dart';
import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../flame/prefabs/signal_pulse.dart';
import '../flame/scene_game.dart';
import '../flame/scene_view.dart';
import '../theme/wire_node_values.dart';
import 'button.dart';
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
  'worktree': '#38BDF8',
  'slice': '#818CF8',
  'verify': '#34D399',
  'pr': '#A78BFA',
  'kernel': '#9E9E9E',
  'draft': '#CE93D8',
};

/// A `*` edge (any publisher) is drawn as a stub this far above its target.
const _stub = 90.0;

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

/// The scene of [node]: 3D icon-badged cards with circular progress rings and 3 Accordant DDD edge styles.
SceneState brainSceneState(BrainSceneNode node, {int revision = 1}) =>
    SceneState(
      name: 'brain',
      revision: revision,
      definition: SceneDefinition(
        nodes: buildBrainGraphSceneNodes(
          node,
          tints: _tints,
          wildcardStubOffset: _stub,
          replayPrefix: brainReplayPrefix,
        ),
        camera: const SceneCamera(zoom: .5),
      ),
    );

/// The brain as a full-bleed graph with live pulses, `Follow Live` camera, and corner minimap.
Widget renderBrainScene(BrainSceneNode node, NodeContext ctx) => LayoutBuilder(
  builder: (context, constraints) => SizedBox(
    height: constraints.hasBoundedHeight && constraints.maxHeight.isFinite
        ? constraints.maxHeight
        : 420,
    child: BrainSceneView(node: node, ctx: ctx),
  ),
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
  String? _lastZoomTarget;
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
    node.selectedId ?? '',
    node.zoomTargetId ?? '',
    for (final n in node.nodes)
      '${n.id}|${n.kind}|${n.label}|${n.subtitle}|${n.status}|${n.x}|${n.y}|${n.select?.key}',
    for (final e in node.synapses)
      if (!_isReplay(e)) '${e.fromId}|${e.toId}|${e.signalLabel}|${e.active}',
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
    final zoomTarget = resolveSceneZoomTarget(widget.node);
    if (zoomTarget != null && zoomTarget != _lastZoomTarget) {
      _lastZoomTarget = zoomTarget;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted) return;
        game.flyToNode(
          zoomTarget,
          targetZoom: widget.node.targetZoom > 0
              ? widget.node.targetZoom
              : null,
        );
      });
    }
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
        SignalPulseComponent(pulse, onTap: _pick, onArrived: pulses.arrived),
      );
    }
    game.notifyLiveNode(added.last.edge.toId);
    game.wake();
  }

  // An edge or a pulse is not a node of the view: the scene's own select command carries its key as the value.
  void _pick(String key) {
    final select = widget.node.select;
    if (select == null || !widget.ctx.interactive) return;
    unawaited(sendPick(select, widget.ctx, key));
  }

  void _input(String id, String kind, double x, double y, String? payload) {
    if (!isTapBrainInput(kind)) return;
    if (isGraphEdgeId(id)) {
      _pick(id);
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
  Widget build(BuildContext context) => SceneView(
    sceneKey: 'brain',
    state: _state,
    game: game,
    onInput: _input,
    onReplayKey: (key, cursor) => unawaited(
      pressCommand(
        key,
        Command(
          target: '.',
          signal: 'creator.replay',
          args: {'cursor': cursor},
          props: null,
        ),
        widget.ctx,
        context,
      ),
    ),
  );
}
