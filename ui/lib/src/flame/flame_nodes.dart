import 'dart:convert';
import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flame/components.dart';
import 'package:flame/effects.dart';
import 'package:flame/events.dart';
import 'package:flame/flame.dart';
import 'package:flame/particles.dart';
import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart'
    hide TextNode;

import 'flame_prefabs.dart';
import 'scene_game.dart';

// --- src/flame/graph_semantics.dart ---
enum AccordantEdgeStyle {
  /// Solid glowing curve + directional arrowhead + flowing signal pulse dot (`───▶`).
  accepted,

  /// Dashed curve + directional arrowhead (`- - ▶`) for parallel/candidate/alternative branches.
  candidate,

  /// Dotted error-colored curve + directional arrowhead + `✕ Rule` guard badge (`─✕─▶`).
  refused,
}

/// Resolved visual & path metadata for a graph edge.
final class GraphEdgeVisualSpec {
  const GraphEdgeVisualSpec({
    required this.style,
    required this.colorHex,
    required this.strokeWidth,
    required this.pathD,
    required this.centerX,
    required this.centerY,
    required this.activePulse,
    this.badgeLabel = '',
  });

  final AccordantEdgeStyle style;
  final String colorHex;
  final double strokeWidth;
  final String pathD;
  final double centerX;
  final double centerY;
  final bool activePulse;
  final String badgeLabel;

  String get styleToken => switch (style) {
    AccordantEdgeStyle.accepted => 'accepted',
    AccordantEdgeStyle.candidate => 'candidate',
    AccordantEdgeStyle.refused => 'refused',
  };
}

final RegExp _percentPattern = RegExp(r'(\d{1,3})\s*%');
final RegExp _rejectedBracketPattern = RegExp(
  r'\[\s*(?:rejected|refused)\s*:\s*([^\]]+)\]',
  caseSensitive: false,
);
final RegExp _rulePrefixPattern = RegExp(
  r'^(?:[✗✕xX]\s*|rule\s*:\s*|refused\s*:\s*|guard\s*:\s*)(.+)$',
  caseSensitive: false,
);

/// Extracts a normalized `0.0..1.0` circular progress fraction from node parameters or labels,
/// or returns `null` when the node carries no explicit percentage progress.
double? parseGraphNodeProgress({
  String? progressParam,
  String? status,
  String? subtitle,
  String? label,
}) {
  if (progressParam != null && progressParam.trim().isNotEmpty) {
    final clean = progressParam.trim();
    if (clean.endsWith('%')) {
      final pct = double.tryParse(clean.substring(0, clean.length - 1).trim());
      if (pct != null) return (pct / 100.0).clamp(0.0, 1.0);
    } else {
      final raw = double.tryParse(clean);
      if (raw != null) {
        return (raw > 1.0 ? raw / 100.0 : raw).clamp(0.0, 1.0);
      }
    }
  }

  for (final candidate in <String?>[status, subtitle, label]) {
    if (candidate == null || candidate.isEmpty) continue;
    final match = _percentPattern.firstMatch(candidate);
    if (match != null) {
      final pct = int.tryParse(match.group(1) ?? '');
      if (pct != null) {
        return (pct.clamp(0, 100) / 100.0).clamp(0.0, 1.0);
      }
    }
  }
  return null;
}

/// Infers a crisp icon slug for a graph node based on its kind, status, id, and label.
String inferGraphNodeIcon({
  required String kind,
  required String status,
  required String id,
  required String label,
}) {
  final lowerId = id.toLowerCase();
  final lowerLabel = label.toLowerCase();
  final lowerKind = kind.trim().toLowerCase();
  final lowerStatus = status.trim().toLowerCase();

  if (lowerStatus == 'error' ||
      lowerStatus == 'failed' ||
      lowerStatus == 'refused') {
    return 'triangle-alert';
  }

  final byKind = switch (lowerKind) {
    'worktree' => 'git-branch',
    'slice' => 'cpu',
    'verify' => 'shield-check',
    'pr' => 'git-pull-request',
    _ => null,
  };
  if (byKind != null) return byKind;

  if (lowerId.contains('pr') ||
      lowerLabel.contains('pull request') ||
      lowerLabel.contains('ship pr')) {
    return 'git-pull-request';
  }
  if (lowerId.contains('verify') || lowerLabel.contains('verify')) {
    return 'shield-check';
  }
  if (lowerId.contains('worktree') ||
      lowerId.startsWith('wt-') ||
      lowerLabel.startsWith('w1') ||
      lowerLabel.startsWith('w2') ||
      lowerLabel.startsWith('w3')) {
    return 'git-branch';
  }
  if (lowerId.contains('slice') || lowerLabel.contains('slice')) {
    return 'cpu';
  }
  if (lowerId.contains('plan') ||
      lowerId.contains('ratify') ||
      lowerLabel.contains('ratify') ||
      lowerLabel.contains('discuss')) {
    return 'sparkles';
  }
  if (lowerId.contains('ino') || lowerLabel == 'ino') {
    return 'ino';
  }

  return switch (lowerKind) {
    'initial' => 'play',
    'state' => 'git-commit-horizontal',
    'app' => 'layout-grid',
    'unit' => 'cpu',
    'draft' => 'sparkles',
    _ => 'hub',
  };
}

/// Resolves the status ring color for a 3D graph node card.
ui.Color resolveGraphStatusRingColor(String status, ui.Color fallback) {
  final s = status.trim().toLowerCase();
  if (s.contains('error') || s.contains('fail') || s.contains('refus')) {
    return const ui.Color(0xFFF87171);
  }
  if (s.contains('done') ||
      s.contains('verif') ||
      s.contains('pass') ||
      s.contains('publish') ||
      s.contains('installed') ||
      s.contains('100%')) {
    return const ui.Color(0xFF34D399);
  }
  if (s.contains('work') ||
      s.contains('run') ||
      s.contains('build') ||
      s.contains('implement') ||
      s.contains('active') ||
      _percentPattern.hasMatch(s)) {
    return const ui.Color(0xFF38BDF8);
  }
  return fallback;
}

/// Classifies an Accordant / Brain edge into `accepted`, `candidate`, or `refused`
/// and extracts any DDD `Rule` refusal badge text.
({AccordantEdgeStyle style, String badgeLabel}) classifyAccordantEdge({
  required String? signalLabel,
  required bool active,
  required bool isSelfLoop,
  required bool sceneHasActiveEdges,
}) {
  final raw = (signalLabel ?? '').trim();
  final lower = raw.toLowerCase();

  final bracketMatch = _rejectedBracketPattern.firstMatch(raw);
  if (bracketMatch != null) {
    final rule = (bracketMatch.group(1) ?? 'Rule').trim();
    return (style: AccordantEdgeStyle.refused, badgeLabel: '✕ $rule');
  }

  if (raw.startsWith('✗') ||
      raw.startsWith('✕') ||
      lower.startsWith('rule:') ||
      lower.startsWith('refused:') ||
      lower.startsWith('guard:') ||
      lower.contains('requires_verified_sha') ||
      lower.contains('overlapping_file_slices') ||
      lower.contains('[refused') ||
      lower.contains('[rejected')) {
    final prefixMatch = _rulePrefixPattern.firstMatch(raw);
    final extracted = prefixMatch?.group(1)?.trim() ?? raw;
    final clean = extracted.isEmpty ? 'Rule' : extracted;
    final shortRule = clean.length > 26 ? '${clean.substring(0, 25)}…' : clean;
    return (style: AccordantEdgeStyle.refused, badgeLabel: '✕ $shortRule');
  }

  if (lower.startsWith('candidate:') ||
      lower.startsWith('alt:') ||
      lower.startsWith('branch:') ||
      lower.contains('[candidate]') ||
      lower.contains('standby')) {
    return (style: AccordantEdgeStyle.candidate, badgeLabel: '');
  }

  if (active) {
    return (style: AccordantEdgeStyle.accepted, badgeLabel: '');
  }

  if (sceneHasActiveEdges) {
    return (
      style: isSelfLoop
          ? AccordantEdgeStyle.refused
          : AccordantEdgeStyle.candidate,
      badgeLabel: isSelfLoop ? '✕ Rule' : '',
    );
  }

  return (style: AccordantEdgeStyle.accepted, badgeLabel: '');
}

/// Computes curved path geometry and Accordant visual metadata between `start` and `end`.
GraphEdgeVisualSpec buildGraphEdgeVisual({
  required Vector2 start,
  required Vector2 end,
  required String? signalLabel,
  required bool active,
  required bool sceneHasActiveEdges,
  required bool isWildcardOrigin,
}) {
  final dx = end.x - start.x;
  final dy = end.y - start.y;
  final dist = math.sqrt(dx * dx + dy * dy);
  final isSelfLoop = dist < 0.5;

  final (:style, :badgeLabel) = classifyAccordantEdge(
    signalLabel: signalLabel,
    active: active,
    isSelfLoop: isSelfLoop,
    sceneHasActiveEdges: sceneHasActiveEdges,
  );

  final colorHex = switch (style) {
    AccordantEdgeStyle.refused => '#EBFF6B6B',
    AccordantEdgeStyle.candidate => '#B894A3B8',
    AccordantEdgeStyle.accepted => active ? '#EE38BDF8' : '#B860A5FA',
  };
  final strokeWidth = switch (style) {
    AccordantEdgeStyle.accepted => active ? 2.8 : 2.2,
    AccordantEdgeStyle.candidate => 2.0,
    AccordantEdgeStyle.refused => 2.1,
  };

  if (isSelfLoop) {
    return GraphEdgeVisualSpec(
      style: style,
      colorHex: style == AccordantEdgeStyle.refused ? '#EBFF6B6B' : '#CCD97706',
      strokeWidth: strokeWidth,
      pathD: 'M -18 -42 C -52 -92 52 -92 18 -42',
      centerX: start.x,
      centerY: start.y - 60,
      activePulse: style == AccordantEdgeStyle.accepted && active,
      badgeLabel: badgeLabel,
    );
  }

  // Trim endpoints to stop cleanly at the 3D card perimeter (~48px half-extent) so directional
  // arrowheads (`───▶`) sit crisply outside the target card rather than underneath it.
  final ux = dx / dist;
  final uy = dy / dist;
  final startTrim = isWildcardOrigin ? 0.0 : math.min(48.0, dist * 0.28);
  final endTrim = math.min(50.0, dist * 0.30);

  final sx = start.x + ux * startTrim;
  final sy = start.y + uy * startTrim;
  final ex = end.x - ux * endTrim;
  final ey = end.y - uy * endTrim;

  final midX = (sx + ex) / 2.0;
  final midY = (sy + ey) / 2.0;
  final spanX = ex - sx;
  final spanY = ey - sy;

  final String pathD;
  if (spanX.abs() >= spanY.abs() * 0.65) {
    final c1x = sx + spanX * 0.42;
    final c1y = sy;
    final c2x = ex - spanX * 0.42;
    final c2y = ey;
    pathD =
        'M ${sx - midX} ${sy - midY} C ${c1x - midX} ${c1y - midY} ${c2x - midX} ${c2y - midY} ${ex - midX} ${ey - midY}';
  } else {
    final c1x = sx;
    final c1y = sy + spanY * 0.42;
    final c2x = ex;
    final c2y = ey - spanY * 0.42;
    pathD =
        'M ${sx - midX} ${sy - midY} C ${c1x - midX} ${c1y - midY} ${c2x - midX} ${c2y - midY} ${ex - midX} ${ey - midY}';
  }

  return GraphEdgeVisualSpec(
    style: style,
    colorHex: colorHex,
    strokeWidth: strokeWidth,
    pathD: pathD,
    centerX: midX,
    centerY: midY,
    activePulse: style == AccordantEdgeStyle.accepted,
    badgeLabel: badgeLabel,
  );
}

/// Builds the [SceneNode] list for a [BrainSceneNode] with 3D icon cards, circular progress
/// rings, and 3 Accordant DDD edge styles (`accepted`, `candidate`, `refused`).
List<SceneNode> buildBrainGraphSceneNodes(
  BrainSceneNode scene, {
  required Map<String, String> tints,
  required double wildcardStubOffset,
  required String replayPrefix,
}) {
  final nodes = <SceneNode>[];
  final at = {for (final n in scene.nodes) n.id: Vector2(n.x, n.y)};
  final nonReplayEdges = [
    for (final e in scene.synapses)
      if (!(e.signalLabel?.startsWith(replayPrefix) ?? false)) e,
  ];
  final sceneHasActiveEdges = nonReplayEdges.any((e) => e.active);

  for (final edge in nonReplayEdges) {
    final to = at[edge.toId];
    if (to == null) continue;
    final isWildcard = edge.fromId == '*';
    if (!isWildcard && !at.containsKey(edge.fromId)) continue;

    final start = isWildcard
        ? to + Vector2(0, -wildcardStubOffset)
        : at[edge.fromId]!.clone();
    final rawLabel = edge.signalLabel ?? '';
    final hashIdx = rawLabel.indexOf('#');
    final alias = hashIdx < 0 ? rawLabel : rawLabel.substring(0, hashIdx);
    final edgeKey = 'edge:${edge.fromId}→${edge.toId}:$rawLabel';

    final visual = buildGraphEdgeVisual(
      start: start,
      end: to,
      signalLabel: rawLabel,
      active: edge.active,
      sceneHasActiveEdges: sceneHasActiveEdges,
      isWildcardOrigin: isWildcard,
    );

    nodes.add(
      SceneNode(
        id: edgeKey,
        type: 'path',
        transform: SceneTransform(x: visual.centerX, y: visual.centerY, z: -1),
        style: visual.colorHex,
        parameters: {
          'd': visual.pathD,
          'stroke': '${visual.strokeWidth}',
          'alias': alias,
          'edgeStyle': visual.styleToken,
          'arrow': 'true',
          'activePulse': visual.activePulse ? 'true' : 'false',
          if (visual.badgeLabel.isNotEmpty) 'badge': visual.badgeLabel,
        },
        inputs: const ['tap'],
      ),
    );
  }

  for (final n in scene.nodes) {
    final isDraft = n.kind == 'draft';
    final progress = parseGraphNodeProgress(
      status: n.status,
      subtitle: n.subtitle,
      label: n.label,
    );
    final icon = inferGraphNodeIcon(
      kind: n.kind,
      status: n.status,
      id: n.id,
      label: n.label,
    );
    final isSelected = scene.selectedId != null && scene.selectedId == n.id;

    nodes.add(
      SceneNode(
        id: n.id,
        type: 'sphere',
        transform: SceneTransform(
          x: n.x,
          y: n.y,
          z: isSelected ? 2 : 1,
          opacity: isDraft ? .5 : 1,
        ),
        style: tints[n.kind] ?? tints['kernel'],
        parameters: {
          'title': n.label,
          'description': n.subtitle ?? n.kind,
          'kind': n.kind,
          'icon': icon,
          'nodeStatus': n.status,
          if (isSelected) 'selected': 'true',
          if (progress != null) 'progress': progress.toStringAsFixed(3),
        },
        inputs: const ['tap'],
      ),
    );
  }

  return nodes;
}

/// Checks whether [id] denotes an edge key (`edge:...`).
bool isGraphEdgeId(String id) => id.startsWith('edge:');

/// Checks whether [node] has a non-empty `zoomTargetId`.
String? resolveSceneZoomTarget(BrainSceneNode node) {
  final target = node.zoomTargetId?.trim();
  return (target == null || target.isEmpty) ? null : target;
}

// --- src/flame/ino_presence.dart ---

const _warning = Color(0xFFF59E0B);

/// The approved green Ino face. This programmatic artwork is a small stand-in
/// until an authored Rive asset is audited; it has no transport or domain state.
final class InoPresence extends StatefulWidget {
  const InoPresence({
    super.key,
    this.state = PresenceState.idle,
    this.size = 92,
  });

  final PresenceState state;
  final double size;

  @override
  State<InoPresence> createState() => _PresenceState();
}

final class _PresenceState extends State<InoPresence>
    with SingleTickerProviderStateMixin {
  late final AnimationController _motion = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 2200),
  );

  bool get _active => switch (widget.state) {
    PresenceState.listening ||
    PresenceState.working ||
    PresenceState.searching => true,
    _ => false,
  };

  void _syncMotion() {
    if (_active && !MediaQuery.disableAnimationsOf(context)) {
      if (!_motion.isAnimating) _motion.repeat(reverse: true);
    } else {
      _motion.stop();
      _motion.value = 0;
    }
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _syncMotion();
  }

  @override
  void didUpdateWidget(InoPresence oldWidget) {
    super.didUpdateWidget(oldWidget);
    _syncMotion();
  }

  @override
  void dispose() {
    _motion.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Semantics(
    image: true,
    label:
        'Ino, ${switch (widget.state) {
          PresenceState.idle => 'ready when you are',
          PresenceState.listening => 'listening',
          PresenceState.working => 'working',
          PresenceState.searching => 'searching',
          PresenceState.waiting => 'waiting for you',
          PresenceState.paused => 'paused',
          PresenceState.attention => 'needs attention',
          PresenceState.disconnected => 'disconnected',
        }}',
    child: RepaintBoundary(
      child: SizedBox.square(
        dimension: widget.size,
        child: AnimatedBuilder(
          animation: _motion,
          builder: (context, _) => CustomPaint(
            painter: _InoPainter(state: widget.state, phase: _motion.value),
          ),
        ),
      ),
    ),
  );
}

/// Paints Ino's face into [size] at [phase] (0..1); the glass sphere draws it while listening and searching.
void paintInoFace(
  Canvas canvas,
  Size size,
  PresenceState state,
  double phase,
) => _InoPainter(state: state, phase: phase).paint(canvas, size);

final class _InoPainter extends CustomPainter {
  const _InoPainter({required this.state, required this.phase});

  final PresenceState state;
  final double phase;

  @override
  void paint(Canvas canvas, Size size) {
    final edge = math.min(size.width, size.height);
    canvas.save();
    canvas.scale(edge / 100);
    final rect = Rect.fromLTWH(7, 7 + phase * 1.4, 86, 86);
    final shape = RRect.fromRectAndRadius(rect, const Radius.circular(29));
    final path = Path()..addRRect(shape);
    canvas.drawShadow(path, const Color(0x243C6D50), 9, false);
    canvas.drawRRect(
      shape,
      Paint()
        ..shader = LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: state == PresenceState.disconnected
              ? const [Color(0xFFE2E8E1), Color(0xFFC5CEC4)]
              : const [Color(0xFFE9F4DF), Color(0xFFC2D9B7)],
        ).createShader(rect),
    );
    canvas.drawRRect(
      shape,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.1
        ..color = const Color(0xFFD0E0C7),
    );
    final face = Paint()
      ..color = state == PresenceState.attention
          ? _warning
          : const Color(0xFF405E48)
      ..strokeWidth = 3.8
      ..strokeCap = StrokeCap.round
      ..style = PaintingStyle.stroke;
    final looking = state == PresenceState.searching ? phase * 3 - 1.5 : 0.0;
    final eyeY = 43 + phase;
    for (final x in [38.0, 62.0]) {
      canvas.drawLine(
        Offset(x + looking, eyeY - 2),
        Offset(x + looking, eyeY + 3),
        face,
      );
    }
    final mouth = Path()..moveTo(43, 61 + phase);
    if (state == PresenceState.attention ||
        state == PresenceState.disconnected) {
      mouth.lineTo(57, 61 + phase);
    } else {
      mouth.quadraticBezierTo(50, 67 + phase, 57, 61 + phase);
    }
    canvas.drawPath(mouth, face..strokeWidth = 2.6);
    if (state == PresenceState.waiting) {
      canvas.drawCircle(const Offset(86, 16), 9, Paint()..color = _warning);
      canvas.drawLine(
        const Offset(86, 11),
        const Offset(86, 17),
        Paint()
          ..color = Colors.white
          ..strokeWidth = 2.2
          ..strokeCap = StrokeCap.round,
      );
      canvas.drawCircle(
        const Offset(86, 21),
        1.2,
        Paint()..color = Colors.white,
      );
    }
    canvas.restore();
  }

  @override
  bool shouldRepaint(_InoPainter oldDelegate) =>
      state != oldDelegate.state || phase != oldDelegate.phase;
}

// --- src/flame/node_schema.dart ---
enum FlameParamKind { text, number, color, json, choice, boolean }

final class FlameParam {
  const FlameParam(
    this.key,
    this.kind, {
    this.required = false,
    this.choices = const [],
  });

  final String key;
  final FlameParamKind kind;
  final bool required;
  final List<String> choices;
}

final class FlameNodeType {
  const FlameNodeType(this.type, this.parameters);

  final String type;
  final List<FlameParam> parameters;

  FlameParam? param(String key) {
    for (final param in parameters) {
      if (param.key == key) return param;
    }
    return null;
  }
}

final class TreeTask {
  const TreeTask(
    this.kind,
    this.minSeconds,
    this.maxSeconds,
    this.minValue,
    this.maxValue,
  );

  final String kind;
  final double minSeconds, maxSeconds, minValue, maxValue;
}

abstract final class FlameSchema {
  static const List<String> gestures = ['tap', 'drag', 'hover'];
  static const List<String> tweenProperties = [
    'x',
    'y',
    'scale',
    'rotation',
    'opacity',
  ];
  static const List<String> tweenCurves = [
    'linear',
    'easeIn',
    'easeOut',
    'easeInOut',
  ];
  static const List<String> moods = [
    'idle',
    'listening',
    'thinking',
    'acting',
    'speaking',
    'error',
    'disconnected',
  ];

  static const List<String> treeKinds = [
    'selector',
    'sequence',
    'repeat',
    'condition',
    'wait',
    'moveTo',
    'scale',
    'fade',
    'pulse',
  ];
  static const List<String> reservedBlackboardKeys = ['running', 'attention'];
  static const int maxTreeNodes = 256;
  static const int maxTreeDepth = 16;
  static const List<TreeTask> treeTasks = [
    TreeTask('wait', 0, 60, 0, 0),
    TreeTask('moveTo', 0, 10, -4096, 4096),
    TreeTask('scale', 0, 10, 0.1, 4),
    TreeTask('fade', 0, 10, 0, 1),
    TreeTask('pulse', 0, 10, 0, 1),
  ];

  static const List<FlameNodeType> nodes = [
    FlameNodeType('circle', [
      FlameParam('radius', FlameParamKind.number),
      FlameParam('color', FlameParamKind.color),
    ]),
    FlameNodeType('rect', [
      FlameParam('width', FlameParamKind.number),
      FlameParam('height', FlameParamKind.number),
      FlameParam('color', FlameParamKind.color),
      FlameParam('corner', FlameParamKind.number),
    ]),
    FlameNodeType('text', [
      FlameParam('text', FlameParamKind.text, required: true),
      FlameParam('size', FlameParamKind.number),
      FlameParam('color', FlameParamKind.color),
      FlameParam(
        'align',
        FlameParamKind.choice,
        choices: ['left', 'center', 'right'],
      ),
    ]),
    FlameNodeType('sprite', [
      FlameParam('src', FlameParamKind.text, required: true),
      FlameParam('width', FlameParamKind.number),
      FlameParam('height', FlameParamKind.number),
    ]),
    FlameNodeType('path', [
      FlameParam('d', FlameParamKind.text, required: true),
      FlameParam('color', FlameParamKind.color),
      FlameParam('step', FlameParamKind.number),
      FlameParam('opacity', FlameParamKind.number),
      FlameParam('kind', FlameParamKind.text),
      FlameParam('weight', FlameParamKind.number),
    ]),
    FlameNodeType('particles', [
      FlameParam('count', FlameParamKind.number),
      FlameParam('speed', FlameParamKind.number),
      FlameParam('color', FlameParamKind.color),
      FlameParam('step', FlameParamKind.number),
      FlameParam('opacity', FlameParamKind.number),
    ]),
    FlameNodeType('group', []),
    FlameNodeType('activity_ring', [
      FlameParam('items', FlameParamKind.json),
      FlameParam('radius', FlameParamKind.number),
    ]),
    FlameNodeType('sphere', [
      FlameParam('icon', FlameParamKind.text, required: true),
      FlameParam('title', FlameParamKind.text),
      FlameParam('description', FlameParamKind.text),
      FlameParam('blackboard', FlameParamKind.json),
      FlameParam('side', FlameParamKind.json),
      FlameParam('main', FlameParamKind.json),
      FlameParam('toolbar', FlameParamKind.json),
      FlameParam(
        'state',
        FlameParamKind.choice,
        choices: ['ready', 'unready', 'error'],
      ),
      FlameParam('error', FlameParamKind.text),
      FlameParam('neuron', FlameParamKind.text),
      FlameParam('focused', FlameParamKind.boolean),
      FlameParam('graphId', FlameParamKind.text),
      FlameParam('kind', FlameParamKind.text),
      FlameParam('level', FlameParamKind.text),
      FlameParam('group', FlameParamKind.text),
      FlameParam('drill', FlameParamKind.text),
    ]),
  ];

  static FlameNodeType? forType(String type) {
    for (final node in nodes) {
      if (node.type == type) return node;
    }
    return null;
  }
}

// --- src/flame/placeholder_node.dart ---
final class PlaceholderNode extends NodeComponent {
  PlaceholderNode(super.node);
  TextPainter? _label;

  @override
  void changed() {
    size.setValues(param('width', 96), param('height', 48));
    super.changed();
    _label = TextPainter(
      text: TextSpan(
        text: typeName,
        style: const TextStyle(color: Color(0xFF9AA4B2), fontSize: 11),
      ),
      textDirection: TextDirection.ltr,
    )..layout(maxWidth: size.x);
  }

  @override
  void render(ui.Canvas canvas) {
    final outline = ui.Paint()
      ..color = const Color(0xFF9AA4B2)
      ..style = ui.PaintingStyle.stroke
      ..strokeWidth = 1;
    const dash = 6.0;
    void dashed(Offset from, Offset to) {
      final length = (to - from).distance;
      final step = (to - from) / length;
      for (var d = 0.0; d < length; d += dash * 2) {
        canvas.drawLine(
          from + step * d,
          from + step * math.min(d + dash, length),
          outline,
        );
      }
    }

    final r = size.toRect();
    dashed(r.topLeft, r.topRight);
    dashed(r.topRight, r.bottomRight);
    dashed(r.bottomRight, r.bottomLeft);
    dashed(r.bottomLeft, r.topLeft);
    final label = _label;
    if (label != null) {
      label.paint(
        canvas,
        Offset((size.x - label.width) / 2, (size.y - label.height) / 2),
      );
    }
  }
}

// --- src/flame/primitive_nodes.dart ---
final class CircleNode extends NodeComponent {
  CircleNode(super.node);

  @override
  void changed() {
    final radius = param('radius', 24);
    size.setAll(radius * 2);
    super.changed();
  }

  @override
  void render(ui.Canvas canvas) =>
      canvas.drawCircle((size / 2).toOffset(), size.x / 2, paint);
}

final class RectNode extends NodeComponent {
  RectNode(super.node);

  @override
  void changed() {
    size.setValues(param('width', 48), param('height', 48));
    super.changed();
  }

  @override
  void render(ui.Canvas canvas) {
    final radius = param('radius', 0);
    canvas.drawRRect(
      ui.RRect.fromRectAndRadius(size.toRect(), ui.Radius.circular(radius)),
      paint,
    );
  }
}

final class TextNode extends NodeComponent {
  TextNode(super.node);
  TextPainter? _painter;

  @override
  void changed() {
    super.changed();
    _painter = TextPainter(
      text: TextSpan(
        text: params['text'] ?? '',
        style: TextStyle(color: color, fontSize: param('size', 16)),
      ),
      textDirection: TextDirection.ltr,
    )..layout();
    size.setValues(_painter!.width, _painter!.height);
  }

  // Text draws through a layer so opacity and OpacityEffect fade it like the shapes.
  @override
  void render(ui.Canvas canvas) {
    final painter = _painter;
    if (painter == null) return;
    canvas.saveLayer(size.toRect(), ui.Paint()..color = paint.color);
    painter.paint(canvas, Offset.zero);
    canvas.restore();
  }
}

final class SpriteNode extends NodeComponent {
  SpriteNode(super.node);
  ui.Image? _image;
  String? _loading;

  @override
  void changed() {
    size.setValues(param('width', 48), param('height', 48));
    super.changed();
    final src = params['src'];
    if (src != null && src != _loading) {
      _loading = src;
      _image = null;
      Flame.images
          .load(src)
          .then((image) {
            if (_loading == src) _image = image;
            if (isMounted) game.wake();
          })
          .catchError((Object _) => _image = null);
    }
  }

  @override
  void render(ui.Canvas canvas) {
    final image = _image;
    if (image == null) {
      // A failed or pending load draws as a rect so the layout holds.
      canvas.drawRect(size.toRect(), paint);
      return;
    }
    canvas.drawImageRect(
      image,
      ui.Rect.fromLTWH(0, 0, image.width.toDouble(), image.height.toDouble()),
      size.toRect(),
      paint,
    );
  }
}

/// `d` is a move/line/cubic list: `M x y L x y C x1 y1 x2 y2 x y Z`.
/// Supports the 3 Accordant DDD edge styles (`accepted`, `candidate`, `refused`),
/// directional arrowheads (`───▶`), flowing signal pulse dots, and `✕ Rule` guard badges.
final class PathNode extends NodeComponent {
  PathNode(super.node);

  ui.Path _path = ui.Path();
  AccordantEdgeStyle? _edgeStyle;
  bool _drawArrow = false;
  bool _activePulse = false;
  String _badgeLabel = '';
  TextPainter? _badgePainter;
  double _pulseElapsed = 0;
  static const double _pulseDuration = 0.85;

  @visibleForTesting
  AccordantEdgeStyle? get accordantStyle => _edgeStyle;

  @visibleForTesting
  String get badgeLabel => _badgeLabel;

  @visibleForTesting
  bool get hasDirectionalArrow => _drawArrow;

  bool get needsPulseFrames =>
      _activePulse &&
      isMounted &&
      game.animations &&
      _pulseElapsed < _pulseDuration;

  @override
  void changed() {
    final raw = parse(params['d'] ?? '');
    final bounds = raw.getBounds();
    _edgeStyle = switch (params['edgeStyle']) {
      'accepted' => AccordantEdgeStyle.accepted,
      'candidate' => AccordantEdgeStyle.candidate,
      'refused' => AccordantEdgeStyle.refused,
      _ => null,
    };
    _drawArrow = params['arrow'] == 'true' || _edgeStyle != null;
    _activePulse = params['activePulse'] == 'true';
    _badgeLabel = params['badge'] ?? '';
    _pulseElapsed = 0;

    if (_edgeStyle != null || _drawArrow) {
      final boxW = math.max(bounds.width, 28.0);
      final boxH = math.max(bounds.height, 28.0);
      size.setValues(boxW, boxH);
      _path = raw.shift(
        Offset(boxW / 2 - bounds.center.dx, boxH / 2 - bounds.center.dy),
      );
    } else if (bounds.left < 0 || bounds.top < 0) {
      final dx = bounds.left < 0 ? -bounds.left : 0.0;
      final dy = bounds.top < 0 ? -bounds.top : 0.0;
      _path = raw.shift(Offset(dx, dy));
      size.setValues(bounds.right + dx, bounds.bottom + dy);
    } else {
      _path = raw;
      size.setValues(bounds.right, bounds.bottom);
    }

    if (_badgeLabel.isNotEmpty) {
      _badgePainter = TextPainter(
        text: TextSpan(
          text: _badgeLabel,
          style: const TextStyle(
            color: Color(0xFFFFE4E6),
            fontSize: 9.5,
            fontWeight: FontWeight.w700,
          ),
        ),
        textDirection: TextDirection.ltr,
        maxLines: 1,
        ellipsis: '…',
      )..layout(maxWidth: 164);
    } else {
      _badgePainter = null;
    }

    super.changed();
    paint
      ..style = params['fill'] == 'true'
          ? ui.PaintingStyle.fill
          : ui.PaintingStyle.stroke
      ..strokeCap = ui.StrokeCap.round
      ..strokeJoin = ui.StrokeJoin.round
      ..strokeWidth = param('stroke', 2);
  }

  @override
  void update(double dt) {
    super.update(dt);
    if (_activePulse && game.animations && _pulseElapsed < _pulseDuration) {
      _pulseElapsed = (_pulseElapsed + dt).clamp(0.0, _pulseDuration);
    }
  }

  static ui.Path parse(String d) {
    final path = ui.Path();
    final tokens = d
        .replaceAllMapped(RegExp('[MLCZmlcz]'), (m) => ' ${m[0]} ')
        .split(RegExp(r'[\s,]+'))
        .where((t) => t.isNotEmpty)
        .toList();
    var command = 'M';
    for (var i = 0; i < tokens.length;) {
      final token = tokens[i];
      if (RegExp('^[MLCZmlcz]\$').hasMatch(token)) {
        command = token.toUpperCase();
        i++;
        if (command == 'Z') path.close();
        continue;
      }
      if (command == 'C') {
        if (i + 5 >= tokens.length) break;
        final x1 = double.tryParse(token) ?? 0;
        final y1 = double.tryParse(tokens[i + 1]) ?? 0;
        final x2 = double.tryParse(tokens[i + 2]) ?? 0;
        final y2 = double.tryParse(tokens[i + 3]) ?? 0;
        final x3 = double.tryParse(tokens[i + 4]) ?? 0;
        final y3 = double.tryParse(tokens[i + 5]) ?? 0;
        path.cubicTo(x1, y1, x2, y2, x3, y3);
        i += 6;
        continue;
      }
      if (i + 1 >= tokens.length) break;
      final x = double.tryParse(token) ?? 0;
      final y = double.tryParse(tokens[i + 1]) ?? 0;
      if (command == 'M') {
        path.moveTo(x, y);
        command = 'L';
      } else {
        path.lineTo(x, y);
      }
      i += 2;
    }
    return path;
  }

  static ui.Path _segmentPattern(
    ui.PathMetric metric,
    double dash,
    double gap,
  ) {
    final out = ui.Path();
    var dist = 0.0;
    final total = metric.length;
    while (dist < total) {
      final end = math.min(dist + dash, total);
      out.addPath(metric.extractPath(dist, end), Offset.zero);
      dist += dash + gap;
    }
    return out;
  }

  static void _drawArrowhead(
    ui.Canvas canvas,
    ui.Tangent endTangent,
    ui.Color arrowColor,
  ) {
    final tip = endTangent.position;
    final dir = endTangent.vector;
    final len = dir.distance;
    if (len < 1e-4) return;
    final ux = dir.dx / len;
    final uy = dir.dy / len;
    const arrowLength = 9.5;
    const arrowHalfW = 4.6;
    final baseX = tip.dx - ux * arrowLength;
    final baseY = tip.dy - uy * arrowLength;
    final nx = -uy;
    final ny = ux;

    final arrow = ui.Path()
      ..moveTo(tip.dx, tip.dy)
      ..lineTo(baseX + nx * arrowHalfW, baseY + ny * arrowHalfW)
      ..lineTo(baseX - nx * arrowHalfW, baseY - ny * arrowHalfW)
      ..close();
    canvas.drawPath(arrow, ui.Paint()..color = arrowColor);
  }

  @override
  void render(ui.Canvas canvas) {
    final edgeStyle = _edgeStyle;
    if (edgeStyle == null && !_drawArrow) {
      canvas.drawPath(_path, paint);
      return;
    }

    final strokeColor = paint.color;
    final strokeW = paint.strokeWidth;
    final metrics = _path.computeMetrics().toList(growable: false);
    if (metrics.isEmpty) return;

    for (final metric in metrics) {
      if (metric.length < 0.5) continue;
      switch (edgeStyle ?? AccordantEdgeStyle.accepted) {
        case AccordantEdgeStyle.accepted:
          canvas.drawPath(
            metric.extractPath(0, metric.length),
            ui.Paint()
              ..style = ui.PaintingStyle.stroke
              ..strokeCap = ui.StrokeCap.round
              ..strokeWidth = strokeW + 3.6
              ..color = strokeColor.withValues(
                alpha: (strokeColor.a * 0.22).clamp(0.0, 1.0),
              ),
          );
          canvas.drawPath(_path, paint);
        case AccordantEdgeStyle.candidate:
          canvas.drawPath(_segmentPattern(metric, 8.0, 6.0), paint);
        case AccordantEdgeStyle.refused:
          canvas.drawPath(_segmentPattern(metric, 2.2, 5.4), paint);
      }

      if (_drawArrow) {
        final endTangent = metric.getTangentForOffset(metric.length);
        if (endTangent != null) {
          _drawArrowhead(canvas, endTangent, strokeColor);
        }
      }

      if (_activePulse &&
          (edgeStyle == null || edgeStyle == AccordantEdgeStyle.accepted)) {
        final phase = game.animations && _pulseElapsed < _pulseDuration
            ? (_pulseElapsed / _pulseDuration).clamp(0.08, 0.92)
            : 0.68;
        final pulseTangent = metric.getTangentForOffset(metric.length * phase);
        if (pulseTangent != null) {
          final p = pulseTangent.position;
          canvas.drawCircle(
            p,
            7.0,
            ui.Paint()
              ..color = strokeColor.withValues(
                alpha: (strokeColor.a * 0.28).clamp(0.0, 1.0),
              ),
          );
          canvas.drawCircle(
            p,
            3.6,
            ui.Paint()
              ..color = const Color(0xFFE0F2FE)
                  .withValues(alpha: strokeColor.a.clamp(0.0, 1.0)),
          );
        }
      }
    }

    if (_badgePainter case final badge?) {
      final firstMetric = metrics.first;
      final midTangent = firstMetric.getTangentForOffset(
        firstMetric.length * 0.5,
      );
      if (midTangent != null) {
        final mid = midTangent.position;
        final pillW = badge.width + 12;
        final pillH = badge.height + 5;
        final rect = Rect.fromCenter(center: mid, width: pillW, height: pillH);
        final rrect = ui.RRect.fromRectAndRadius(
          rect,
          const ui.Radius.circular(8),
        );
        canvas.drawRRect(rrect, ui.Paint()..color = const Color(0xEE2B0E14));
        canvas.drawRRect(
          rrect,
          ui.Paint()
            ..style = ui.PaintingStyle.stroke
            ..strokeWidth = 1.1
            ..color = strokeColor,
        );
        badge.paint(
          canvas,
          Offset(mid.dx - badge.width / 2, mid.dy - badge.height / 2),
        );
      }
    }
  }
}

final class ParticlesNode extends NodeComponent {
  ParticlesNode(super.node);
  final _random = math.Random();

  @override
  void changed() {
    size.setAll(param('radius', 48) * 2);
    super.changed();
  }

  @override
  void update(double dt) {
    super.update(dt);
    if (children.whereType<ParticleSystemComponent>().isEmpty) {
      add(ParticleSystemComponent(position: size / 2, particle: _burst()));
    }
  }

  Particle _burst() {
    final speed = param('speed', 60);
    final dot = ui.Paint()..color = sceneColor(params['color']) ?? color;
    return Particle.generate(
      count: param('count', 24).round(),
      lifespan: 1.5,
      generator: (_) {
        final direction = _random.nextDouble() * 2 * math.pi;
        return AcceleratedParticle(
          speed:
              Vector2(math.cos(direction), math.sin(direction)) *
              speed *
              (0.5 + _random.nextDouble()),
          child: CircleParticle(radius: param('dot', 2), paint: dot),
        );
      },
    );
  }

  @override
  void render(ui.Canvas canvas) {}
}

final class GroupNode extends NodeComponent {
  GroupNode(super.node);

  @override
  void render(ui.Canvas canvas) {}
}

// --- src/flame/node_components.dart ---
Color? sceneColor(String? style) {
  if (style == null || !style.startsWith('#')) return null;
  final hex = style.substring(1);
  final value = int.tryParse(hex, radix: 16);
  if (value == null) return null;
  return switch (hex.length) {
    6 => Color(0xFF000000 | value),
    8 => Color(value),
    _ => null,
  };
}

/// One scene node drawn in Flame, set from its definition.
abstract class NodeComponent extends PositionComponent
    with HasPaint, HasGameReference<SceneGame>, TapCallbacks, DragCallbacks {
  NodeComponent(SceneNode node)
    : nodeId = node.id,
      typeName = node.type,
      super(anchor: Anchor.center) {
    configure(node);
  }

  final String nodeId;
  final String typeName;
  String? style;
  Map<String, String> params = const {};
  List<String> inputs = const [];
  List<SceneTween> _tweens = const [];
  BehaviorTreeDef? treeDef;
  String _tweenKey = '';

  Color get color => sceneColor(style) ?? const Color(0xFFFFFFFF);

  double param(String name, double fallback) =>
      double.tryParse(params[name] ?? '') ?? fallback;

  /// Number of times this node's tweens were (re)started.
  @visibleForTesting
  int restarts = 0;
  String _signature = '';

  /// Sets the component to [node]. An unchanged node is a no-op, so running tweens and trees keep going
  /// across refreshes.
  void configure(SceneNode node) {
    final signature = _signatureOf(node);
    if (signature == _signature) return;
    _signature = signature;
    style = node.style;
    params = Map.of(node.parameters);
    inputs = node.inputs;
    treeDef = node.tree;
    _transform(node.transform);
    _tweens = node.tweens;
    changed();
  }

  static String _signatureOf(SceneNode node) {
    final t = node.transform;
    final params = node.parameters.entries.toList()
      ..sort((a, b) => a.key.compareTo(b.key));
    return [
      node.style,
      params.map((e) => '${e.key}=${e.value}').join('&'),
      node.inputs.join(','),
      '${t.x},${t.y},${t.z},${t.scale},${t.rotation},${t.opacity}',
      _tweenKeyOf(node.tweens),
      node.tree == null ? '' : jsonEncode(node.tree!.toJson()),
    ].join('|');
  }

  static String _tweenKeyOf(List<SceneTween> tweens) => tweens
      .map(
        (t) =>
            '${t.property}|${t.from}|${t.to}|${t.milliseconds}|${t.curve}|${t.repeat}',
      )
      .join(';');

  void _transform(SceneTransform t) {
    position.setValues(t.x, t.y);
    scale.setAll(t.scale);
    angle = t.rotation;
    priority = t.z.round();
    _opacity = t.opacity;
  }

  double _opacity = 1;

  /// Recomputes size and paint after the definition or a patch changed; subclasses add to it.
  void changed() {
    paint.color = color;
    opacity = _opacity.clamp(0, 1);
    final key = _tweenKeyOf(_tweens);
    if (key != _tweenKey) {
      _tweenKey = key;
      _runTweens();
    }
  }

  void _runTweens() {
    restarts++;
    for (final effect in children.whereType<Effect>().toList()) {
      effect.removeFromParent();
    }
    for (final tween in _tweens) {
      final controller = EffectController(
        duration: math.max(tween.milliseconds, 1) / 1000,
        curve: _curve(tween.curve),
        infinite: tween.repeat,
        alternate: tween.repeat,
      );
      final effect = switch (tween.property) {
        'x' => (() {
          position.x = tween.from;
          return MoveEffect.to(Vector2(tween.to, position.y), controller);
        })(),
        'y' => (() {
          position.y = tween.from;
          return MoveEffect.to(Vector2(position.x, tween.to), controller);
        })(),
        'scale' => (() {
          scale.setAll(tween.from);
          return ScaleEffect.to(Vector2.all(tween.to), controller);
        })(),
        'opacity' => (() {
          opacity = tween.from.clamp(0, 1);
          return OpacityEffect.to(tween.to.clamp(0, 1), controller);
        })(),
        'rotation' => (() {
          angle = tween.from;
          return RotateEffect.to(tween.to, controller);
        })(),
        _ => null,
      };
      if (effect != null) add(effect);
    }
  }

  static Curve _curve(String name) => switch (name) {
    'linear' => Curves.linear,
    'easeIn' => Curves.easeIn,
    'easeOut' => Curves.easeOut,
    'bounce' || 'bounceOut' => Curves.bounceOut,
    'elastic' || 'elasticOut' => Curves.elasticOut,
    _ => Curves.easeInOut,
  };

  @override
  bool containsLocalPoint(Vector2 point) =>
      inputs.isNotEmpty && super.containsLocalPoint(point);

  @override
  void onTapUp(TapUpEvent event) {
    game.wake();
    if (inputs.contains('tap')) {
      game.report(this, 'tap', event.localPosition);
    } else {
      event.continuePropagation = true;
    }
  }

  /// Whether this node takes drags (and stops them propagating).
  bool get draggable => inputs.contains('drag');

  /// Whether a drag moves the node and is reported; a prefab may take drags as presentation only.
  bool get reportsDrag => inputs.contains('drag');

  @override
  void onDragStart(DragStartEvent event) {
    super.onDragStart(event);
    game.wake();
    if (!draggable) event.continuePropagation = true;
  }

  @override
  void onDragEnd(DragEndEvent event) {
    super.onDragEnd(event);
    game.wake();
    if (reportsDrag) {
      game.report(
        this,
        'drag',
        position,
        '${event.velocity.x},${event.velocity.y}',
      );
    }
  }

  @override
  void onDragUpdate(DragUpdateEvent event) {
    game.wake();
    if (reportsDrag) position.add(event.localDelta);
  }
}

abstract final class NodeComponents {
  /// The component for a known primitive or prefab type; null for anything else.
  static NodeComponent? build(SceneNode node) => switch (node.type) {
    'circle' => CircleNode(node),
    'rect' => RectNode(node),
    'text' => TextNode(node),
    'sprite' => SpriteNode(node),
    'path' => PathNode(node),
    'particles' => ParticlesNode(node),
    'group' => GroupNode(node),
    'activity_ring' => ActivityRing(node),
    'sphere' => SphereComponent(node),
    _ => null,
  };
}
