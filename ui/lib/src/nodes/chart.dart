import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../design/cards/chart_card.dart';
import '../theme/wire_node_values.dart';
import 'node_common.dart';
import 'node_context.dart';

Widget renderChart(ChartNode node, NodeContext ctx) {
  final select = node.select;
  return ChartCard(
    source: cardSource(node.from),
    kind: parseChartKind(node.chartKind),
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
    onSelectPoint: ctx.interactive && select != null
        ? (index) {
            if (index >= 0 && index < node.points.length) {
              sendPick(select, ctx, node.points[index].label);
            }
          }
        : null,
  );
}
