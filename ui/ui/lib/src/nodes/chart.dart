import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../design/cards/chart_card.dart';
import 'node_common.dart';

Widget renderChart(ChartNode node, NodeContext ctx) => ChartCard(
  source: cardSource(node.from),
  kind: node.chartKind == 'line' ? ChartKind.line : ChartKind.columns,
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
);
