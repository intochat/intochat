import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flame/components.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

/// Canonical Accordant DDD edge styles rendered in the 3D Flame Graph.
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
