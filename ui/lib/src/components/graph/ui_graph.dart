import 'dart:math' as math;

import 'package:flutter/material.dart';

/// Interactive Knowledge Graph component displaying nodes, edges,
/// and an interactive property inspector for the selected node.
class UiGraph extends StatefulWidget {
  const UiGraph({super.key, required this.part, this.height = 360});

  final UiGraphPart part;
  final double height;

  @override
  State<UiGraph> createState() => _UiGraphState();
}

class _UiGraphState extends State<UiGraph> {
  String? _selectedNodeId;

  @override
  void initState() {
    super.initState();
    if (widget.part.nodes.isNotEmpty) {
      _selectedNodeId = widget.part.nodes.first.id;
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final selectedNode = widget.part.nodes.cast<UiGraphNode?>().firstWhere(
      (n) => n?.id == _selectedNodeId,
      orElse: () => null,
    );

    return Container(
      height: widget.height,
      decoration: BoxDecoration(
        color: isDark ? const Color(0xFF1E1E24) : const Color(0xFFF7F8FA),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(
          color: isDark ? const Color(0xFF33333F) : const Color(0xFFE2E4E8),
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _buildHeader(theme, isDark),
          const Divider(height: 1, thickness: 1),
          Expanded(
            child: Row(
              children: [
                Expanded(flex: 3, child: _buildGraphCanvas(isDark)),
                if (selectedNode != null) ...[
                  const VerticalDivider(width: 1, thickness: 1),
                  Expanded(
                    flex: 2,
                    child: _buildInspector(selectedNode, theme, isDark),
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildHeader(ThemeData theme, bool isDark) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
      child: Row(
        children: [
          Icon(Icons.hub_outlined, size: 18, color: theme.colorScheme.primary),
          const SizedBox(width: 8),
          Text(
            widget.part.title,
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
          const Spacer(),
          Text(
            '${widget.part.nodes.length} nodes · ${widget.part.edges.length} edges',
            style: theme.textTheme.bodySmall?.copyWith(
              color: isDark ? Colors.grey[400] : Colors.grey[600],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildGraphCanvas(bool isDark) {
    if (widget.part.nodes.isEmpty) {
      return const Center(child: Text('No nodes in graph'));
    }

    return LayoutBuilder(
      builder: (context, constraints) {
        final center = Offset(
          constraints.maxWidth / 2,
          constraints.maxHeight / 2,
        );
        final radius =
            math.min(constraints.maxWidth, constraints.maxHeight) * 0.38;

        // Position nodes evenly in a circle for deterministic, stable visualization
        final nodePositions = <String, Offset>{};
        final nodeCount = widget.part.nodes.length;
        for (var i = 0; i < nodeCount; i++) {
          final angle = (2 * math.pi / nodeCount) * i - (math.pi / 2);
          final pos = nodeCount == 1
              ? center
              : Offset(
                  center.dx + radius * math.cos(angle),
                  center.dy + radius * math.sin(angle),
                );
          nodePositions[widget.part.nodes[i].id] = pos;
        }

        return Stack(
          children: [
            CustomPaint(
              size: Size(constraints.maxWidth, constraints.maxHeight),
              painter: _GraphEdgePainter(
                edges: widget.part.edges,
                nodePositions: nodePositions,
                selectedNodeId: _selectedNodeId,
                isDark: isDark,
              ),
            ),
            for (final node in widget.part.nodes)
              _buildNodeWidget(node, nodePositions[node.id] ?? center, isDark),
          ],
        );
      },
    );
  }

  Widget _buildNodeWidget(UiGraphNode node, Offset position, bool isDark) {
    final isSelected = node.id == _selectedNodeId;
    final primaryColor = Theme.of(context).colorScheme.primary;

    return Positioned(
      left: position.dx - 40,
      top: position.dy - 22,
      child: GestureDetector(
        onTap: () {
          setState(() {
            _selectedNodeId = node.id;
          });
        },
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 180),
          width: 80,
          padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 4),
          decoration: BoxDecoration(
            color: isSelected
                ? primaryColor
                : (isDark ? const Color(0xFF2A2B36) : Colors.white),
            borderRadius: BorderRadius.circular(8),
            border: Border.all(
              color: isSelected
                  ? primaryColor
                  : (isDark
                        ? const Color(0xFF444558)
                        : const Color(0xFFCBD0D8)),
              width: isSelected ? 2 : 1,
            ),
            boxShadow: [
              if (isSelected)
                BoxShadow(
                  color: primaryColor.withValues(alpha: 0.35),
                  blurRadius: 8,
                  offset: const Offset(0, 2),
                ),
            ],
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                node.label.isNotEmpty ? node.label : node.id,
                textAlign: TextAlign.center,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontSize: 11,
                  fontWeight: isSelected ? FontWeight.bold : FontWeight.w500,
                  color: isSelected
                      ? Colors.white
                      : (isDark ? Colors.white : Colors.black87),
                ),
              ),
              const SizedBox(height: 2),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
                decoration: BoxDecoration(
                  color: isSelected
                      ? Colors.white.withValues(alpha: 0.2)
                      : (isDark ? Colors.black26 : const Color(0xFFEEF0F4)),
                  borderRadius: BorderRadius.circular(4),
                ),
                child: Text(
                  node.type,
                  style: TextStyle(
                    fontSize: 9,
                    color: isSelected
                        ? Colors.white70
                        : (isDark ? Colors.grey[400] : Colors.grey[600]),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildInspector(UiGraphNode node, ThemeData theme, bool isDark) {
    final connectedEdges = widget.part.edges
        .where((e) => e.source == node.id || e.target == node.id)
        .toList(growable: false);

    return Container(
      padding: const EdgeInsets.all(12),
      color: isDark ? const Color(0xFF18181D) : const Color(0xFFFAFAFC),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  node.label.isNotEmpty ? node.label : node.id,
                  style: theme.textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.bold,
                  ),
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                decoration: BoxDecoration(
                  color: theme.colorScheme.primary.withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(6),
                ),
                child: Text(
                  node.type,
                  style: TextStyle(
                    fontSize: 10,
                    fontWeight: FontWeight.w600,
                    color: theme.colorScheme.primary,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 4),
          Text(
            'ID: ${node.id} · Weight: ${node.weight.toStringAsFixed(2)}',
            style: theme.textTheme.bodySmall?.copyWith(
              color: isDark ? Colors.grey[400] : Colors.grey[600],
              fontSize: 11,
            ),
          ),
          const Divider(height: 16),
          Text(
            'Properties',
            style: theme.textTheme.labelMedium?.copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 4),
          if (node.properties.isEmpty)
            Text(
              'No properties defined.',
              style: TextStyle(
                fontSize: 11,
                fontStyle: FontStyle.italic,
                color: isDark ? Colors.grey[500] : Colors.grey[600],
              ),
            )
          else
            Expanded(
              flex: 1,
              child: ListView(
                shrinkWrap: true,
                children: [
                  for (final entry in node.properties.entries)
                    Padding(
                      padding: const EdgeInsets.symmetric(vertical: 2),
                      child: Row(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            '${entry.key}: ',
                            style: const TextStyle(
                              fontSize: 11,
                              fontWeight: FontWeight.w600,
                            ),
                          ),
                          Expanded(
                            child: Text(
                              entry.value,
                              style: const TextStyle(fontSize: 11),
                            ),
                          ),
                        ],
                      ),
                    ),
                ],
              ),
            ),
          const Divider(height: 16),
          Text(
            'Connections (${connectedEdges.length})',
            style: theme.textTheme.labelMedium?.copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 4),
          Expanded(
            flex: 1,
            child: connectedEdges.isEmpty
                ? Text(
                    'No connections.',
                    style: TextStyle(
                      fontSize: 11,
                      fontStyle: FontStyle.italic,
                      color: isDark ? Colors.grey[500] : Colors.grey[600],
                    ),
                  )
                : ListView.builder(
                    itemCount: connectedEdges.length,
                    itemBuilder: (context, index) {
                      final edge = connectedEdges[index];
                      final isOut = edge.source == node.id;
                      final otherId = isOut ? edge.target : edge.source;
                      return Padding(
                        padding: const EdgeInsets.symmetric(vertical: 2),
                        child: Text(
                          '${isOut ? "→" : "←"} [${edge.relationship}] $otherId',
                          style: const TextStyle(fontSize: 11),
                          overflow: TextOverflow.ellipsis,
                        ),
                      );
                    },
                  ),
          ),
        ],
      ),
    );
  }
}

class _GraphEdgePainter extends CustomPainter {
  const _GraphEdgePainter({
    required this.edges,
    required this.nodePositions,
    required this.selectedNodeId,
    required this.isDark,
  });

  final List<UiGraphEdge> edges;
  final Map<String, Offset> nodePositions;
  final String? selectedNodeId;
  final bool isDark;

  @override
  void paint(Canvas canvas, Size size) {
    for (final edge in edges) {
      final src = nodePositions[edge.source];
      final dst = nodePositions[edge.target];
      if (src == null || dst == null) {
        continue;
      }

      final isConnectedToSelected =
          edge.source == selectedNodeId || edge.target == selectedNodeId;

      final paint = Paint()
        ..color = isConnectedToSelected
            ? const Color(0xFF3D7EFF)
            : (isDark ? const Color(0xFF4A4B5A) : const Color(0xFFCCD0D9))
        ..strokeWidth = isConnectedToSelected ? 2.0 : 1.2
        ..style = PaintingStyle.stroke;

      canvas.drawLine(src, dst, paint);
    }
  }

  @override
  bool shouldRepaint(covariant _GraphEdgePainter oldDelegate) {
    return oldDelegate.selectedNodeId != selectedNodeId ||
        oldDelegate.edges != edges ||
        oldDelegate.nodePositions != nodePositions ||
        oldDelegate.isDark != isDark;
  }
}

final class UiGraphNode {
  const UiGraphNode({
    required this.id,
    required this.label,
    required this.type,
    this.weight = 1.0,
    this.properties = const {},
  });

  final String id;
  final String label;
  final String type;
  final double weight;
  final Map<String, String> properties;

  factory UiGraphNode.fromJson(Map<String, dynamic> json) => UiGraphNode(
    id: json['id'] as String? ?? '',
    label: json['label'] as String? ?? '',
    type: json['type'] as String? ?? 'concept',
    weight: (json['weight'] as num?)?.toDouble() ?? 1.0,
    properties: json['properties'] is Map
        ? (json['properties'] as Map).map(
            (k, v) => MapEntry(k.toString(), v?.toString() ?? ''),
          )
        : const {},
  );

  Map<String, Object?> toJson() => {
    'id': id,
    'label': label,
    'type': type,
    'weight': weight,
    'properties': properties,
  };
}

final class UiGraphEdge {
  const UiGraphEdge({
    required this.id,
    required this.source,
    required this.target,
    required this.relationship,
    this.weight = 1.0,
    this.properties = const {},
  });

  final String id;
  final String source;
  final String target;
  final String relationship;
  final double weight;
  final Map<String, String> properties;

  factory UiGraphEdge.fromJson(Map<String, dynamic> json) => UiGraphEdge(
    id: json['id'] as String? ?? '',
    source: json['source'] as String? ?? '',
    target: json['target'] as String? ?? '',
    relationship: json['relationship'] as String? ?? 'relates_to',
    weight: (json['weight'] as num?)?.toDouble() ?? 1.0,
    properties: json['properties'] is Map
        ? (json['properties'] as Map).map(
            (k, v) => MapEntry(k.toString(), v?.toString() ?? ''),
          )
        : const {},
  );

  Map<String, Object?> toJson() => {
    'id': id,
    'source': source,
    'target': target,
    'relationship': relationship,
    'weight': weight,
    'properties': properties,
  };
}

final class UiGraphPart {
  const UiGraphPart({
    this.title = 'Knowledge Graph',
    this.nodes = const [],
    this.edges = const [],
  });

  static const kindName = 'graph';

  final String title;
  final List<UiGraphNode> nodes;
  final List<UiGraphEdge> edges;

  String get kind => kindName;

  String get copyText =>
      '$title (${nodes.length} nodes, ${edges.length} edges)';

  factory UiGraphPart.fromMetadata(Map<String, dynamic> metadata) {
    final rawNodes = metadata['nodes'];
    final nodes = rawNodes is List
        ? rawNodes
              .whereType<Map>()
              .map((n) => UiGraphNode.fromJson(Map<String, dynamic>.from(n)))
              .toList(growable: false)
        : const <UiGraphNode>[];

    final rawEdges = metadata['edges'];
    final edges = rawEdges is List
        ? rawEdges
              .whereType<Map>()
              .map((e) => UiGraphEdge.fromJson(Map<String, dynamic>.from(e)))
              .toList(growable: false)
        : const <UiGraphEdge>[];

    return UiGraphPart(
      title: metadata['title'] as String? ?? 'Knowledge Graph',
      nodes: nodes,
      edges: edges,
    );
  }

  Map<String, Object?> toMetadata() => {
    'kind': kindName,
    'title': title,
    'nodes': [for (final node in nodes) node.toJson()],
    'edges': [for (final edge in edges) edge.toJson()],
  };
}
