/// Dart mirrors of `DigitalBrain.Graph` wire records
/// (`digitalbrain/src/DigitalBrain.Graph/GraphRecords.cs`). Keep both sides aligned.
library;

/// What a node of the brain graph is. Values match the C# enum order.
enum GraphNodeKind {
  brain,
  neuron,
  app,
  operation,
  reaction,
  contract,
  signal,
  connection,
  fact,
  entity,
  external,
  task,
  step,
  run,
}

/// What an edge means, from its source to its target. Values match the C# enum order.
enum GraphEdgeKind {
  owns,
  implements,
  calls,
  subscribes,
  publishes,
  grants,
  relates,
  mentions,
  subtask,
  blocks,
  worksOn,
  executes,
  produced,
}

/// How much of the graph a view shows.
enum GraphLevel { brain, cluster, neurons }

/// A group of nodes shown as one node at the brain level.
class GraphGroup {
  const GraphGroup({
    required this.id,
    required this.label,
    required this.kind,
    required this.size,
    required this.cycle,
  });

  factory GraphGroup.fromJson(Map<String, Object?> json) => GraphGroup(
    id: json['id'] as String,
    label: json['label'] as String,
    kind: GraphNodeKind.values[json['kind'] as int],
    size: json['size'] as int,
    cycle: json['cycle'] as bool,
  );

  final String id;
  final String label;
  final GraphNodeKind kind;
  final int size;
  final bool cycle;
}

/// One edge, expanded from the compressed sparse row arrays.
class GraphEdge {
  const GraphEdge(this.from, this.to, this.kind, this.weight);

  /// Node index the edge leaves.
  final int from;

  /// Node index the edge enters.
  final int to;
  final GraphEdgeKind kind;
  final double weight;
}

/// The graph in compressed sparse row form. Node `i` has the out-edges
/// `col[rowStart[i]..rowStart[i+1])`.
class GraphSnapshot {
  const GraphSnapshot({
    required this.revision,
    required this.ids,
    required this.kinds,
    required this.labels,
    required this.group,
    required this.rowStart,
    required this.col,
    required this.edgeKinds,
    required this.weights,
    required this.groups,
  });

  factory GraphSnapshot.fromJson(Map<String, Object?> json) => GraphSnapshot(
    revision: json['revision'] as int,
    ids: (json['ids'] as List).cast<String>(),
    kinds: (json['kinds'] as List)
        .cast<int>()
        .map((k) => GraphNodeKind.values[k])
        .toList(growable: false),
    labels: (json['labels'] as List).cast<String>(),
    group: (json['group'] as List).cast<int>(),
    rowStart: (json['rowStart'] as List).cast<int>(),
    col: (json['col'] as List).cast<int>(),
    edgeKinds: (json['edgeKinds'] as List)
        .cast<int>()
        .map((k) => GraphEdgeKind.values[k])
        .toList(growable: false),
    weights: (json['weights'] as List)
        .cast<num>()
        .map((w) => w.toDouble())
        .toList(growable: false),
    groups: (json['groups'] as List)
        .cast<Map<String, Object?>>()
        .map(GraphGroup.fromJson)
        .toList(growable: false),
  );

  final int revision;
  final List<String> ids;
  final List<GraphNodeKind> kinds;
  final List<String> labels;

  /// Index into [groups] per node, or -1.
  final List<int> group;
  final List<int> rowStart;
  final List<int> col;
  final List<GraphEdgeKind> edgeKinds;
  final List<double> weights;
  final List<GraphGroup> groups;

  int get nodeCount => ids.length;

  int get edgeCount => col.length;

  int indexOf(String id) => ids.indexOf(id);

  /// Every edge as a flat list, for renderers that draw lines.
  Iterable<GraphEdge> get edges sync* {
    for (var from = 0; from < nodeCount; from++) {
      for (var e = rowStart[from]; e < rowStart[from + 1]; e++) {
        yield GraphEdge(from, col[e], edgeKinds[e], weights[e]);
      }
    }
  }
}

/// One thing a rule found.
class GraphFinding {
  const GraphFinding({
    required this.rule,
    required this.nodes,
    required this.message,
  });

  factory GraphFinding.fromJson(Map<String, Object?> json) => GraphFinding(
    rule: json['rule'] as String,
    nodes: (json['nodes'] as List).cast<String>(),
    message: json['message'] as String,
  );

  final String rule;
  final List<String> nodes;
  final String message;
}

/// What the algorithms say about a snapshot, by node index.
class GraphAnalysis {
  const GraphAnalysis({
    required this.revision,
    required this.component,
    required this.scc,
    required this.community,
    required this.pageRank,
    required this.hub,
    required this.authority,
    required this.findings,
  });

  factory GraphAnalysis.fromJson(Map<String, Object?> json) => GraphAnalysis(
    revision: json['revision'] as int,
    component: (json['component'] as List).cast<int>(),
    scc: (json['scc'] as List).cast<int>(),
    community: (json['community'] as List).cast<int>(),
    pageRank: (json['pageRank'] as List)
        .cast<num>()
        .map((v) => v.toDouble())
        .toList(growable: false),
    hub: (json['hub'] as List)
        .cast<num>()
        .map((v) => v.toDouble())
        .toList(growable: false),
    authority: (json['authority'] as List)
        .cast<num>()
        .map((v) => v.toDouble())
        .toList(growable: false),
    findings: (json['findings'] as List)
        .cast<Map<String, Object?>>()
        .map(GraphFinding.fromJson)
        .toList(growable: false),
  );

  final int revision;
  final List<int> component;
  final List<int> scc;
  final List<int> community;
  final List<double> pageRank;
  final List<double> hub;
  final List<double> authority;
  final List<GraphFinding> findings;
}

/// A path through the graph.
class GraphPath {
  const GraphPath({required this.nodes, required this.edges});

  factory GraphPath.fromJson(Map<String, Object?> json) => GraphPath(
    nodes: (json['nodes'] as List).cast<String>(),
    edges: (json['edges'] as List)
        .cast<int>()
        .map((k) => GraphEdgeKind.values[k])
        .toList(growable: false),
  );

  final List<String> nodes;
  final List<GraphEdgeKind> edges;
}
