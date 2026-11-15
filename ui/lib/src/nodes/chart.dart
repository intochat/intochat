import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../design/cards/chart_card.dart';
import 'node_common.dart';
import 'node_context.dart';

Widget renderChart(ChartNode node, NodeContext ctx) {
  final actionKey = ctx.nodeKeys['chart:${node.title}'];
  return ChartCard(
    source: cardSource(node.from),
    kind: switch (node.chartKind.trim().toLowerCase()) {
      'line' => ChartKind.line,
      'area' => ChartKind.area,
      'pie' => ChartKind.pie,
      'bar' || 'bars' || 'column' || 'columns' || _ => ChartKind.columns,
    },
    data: ChartData(
      title: node.title,
      points: [
        for (final point in node.points) ChartPoint(point.label, point.value),
      ],
      from: node.from,
      prefix: node.prefix,
      compare: node.comparePoints,
      seriesName: node.seriesName,
      compareName: node.compareName,
      sourceNeuronUri: node.sourceNeuronUri,
    ),
    state: node.points.isEmpty ? ChartState.empty : ChartState.ready,
    onSelectPoint: ctx.interactive && actionKey != null && actionKey.isNotEmpty
        ? (index) {
            if (index >= 0 && index < node.points.length) {
              ctx.activate(actionKey, value: node.points[index].label);
            }
          }
        : null,
  );
}
