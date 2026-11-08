import 'dart:math' as math;

import '../brain_scene_3d.dart';
import 'graph_records.dart';

/// What [BrainScene3D] draws for one [GraphSnapshot]: a node per graph node (the brain node left out)
/// and a synapse per edge. Groups sit on a ring, members on a small circle around their group,
/// and each group gets its own depth so the 3D view separates them.
({List<BrainNode3D> nodes, List<BrainSynapse3D> synapses})
brainSceneFromSnapshot(GraphSnapshot g) {
  final groups = g.groups.length;
  final members = <int, List<int>>{};
  final loose = <int>[];
  for (var i = 0; i < g.nodeCount; i++) {
    if (g.kinds[i] == GraphNodeKind.brain) continue;
    final group = g.group[i];
    if (group < 0 || group >= groups) {
      loose.add(i);
    } else {
      members.putIfAbsent(group, () => []).add(i);
    }
  }
  final positions = <int, (double, double, double)>{};
  members.forEach((group, list) {
    final centre = groups <= 1 ? (0.0, 0.0) : _onCircle(group, groups, 0.6);
    final depth = groups <= 1 ? 0.0 : (group / groups) - 0.5;
    if (list.length == 1) {
      positions[list.single] = (centre.$1, centre.$2, depth);
      return;
    }
    final radius = math.min(0.28, 0.06 + 0.015 * list.length);
    for (var k = 0; k < list.length; k++) {
      final offset = _onCircle(k, list.length, radius);
      positions[list[k]] = (
        centre.$1 + offset.$1,
        centre.$2 + offset.$2,
        depth,
      );
    }
  });
  for (var k = 0; k < loose.length; k++) {
    final p = _onCircle(k, loose.length, 0.95);
    positions[loose[k]] = (p.$1, p.$2, 0.0);
  }

  final activeTargets = <int>{
    for (final edge in g.edges)
      if (edge.kind == GraphEdgeKind.worksOn &&
          g.kinds[edge.from] == GraphNodeKind.run)
        edge.to,
  };

  final nodes = <BrainNode3D>[
    for (final entry in positions.entries)
      BrainNode3D(
        id: g.ids[entry.key],
        neuronUri: 'neuron://${g.ids[entry.key]}',
        label: g.labels[entry.key],
        module: g.group[entry.key] >= 0 && g.group[entry.key] < groups
            ? g.groups[g.group[entry.key]].label
            : g.kinds[entry.key].name,
        position: entry.value,
        status:
            activeTargets.contains(entry.key) ||
                g.kinds[entry.key] == GraphNodeKind.run
            ? BrainNodeStatus.working
            : (g.group[entry.key] >= 0 &&
                      g.group[entry.key] < groups &&
                      g.groups[g.group[entry.key]].cycle
                  ? BrainNodeStatus.warning
                  : BrainNodeStatus.healthy),
        signalsPerMin: g.rowStart[entry.key + 1] - g.rowStart[entry.key],
      ),
  ];
  final synapses = <BrainSynapse3D>[
    for (final edge in g.edges)
      if (positions.containsKey(edge.from) && positions.containsKey(edge.to))
        BrainSynapse3D(
          fromId: g.ids[edge.from],
          toId: g.ids[edge.to],
          signalLabel: edge.kind.name,
          active: edge.kind == GraphEdgeKind.worksOn,
        ),
  ];
  return (nodes: nodes, synapses: synapses);
}

(double, double) _onCircle(int index, int count, double radius) {
  if (count <= 1) return (0, 0);
  final angle = (math.pi * 2 * index / count) - (math.pi / 2);
  return (math.cos(angle) * radius, math.sin(angle) * radius);
}
