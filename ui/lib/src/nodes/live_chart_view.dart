import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'chart.dart';
import 'node_context.dart';

/// What a live chart neuron draws: the points from the neuron's view; the
/// title and kind from the child's props. It reuses the `chart` data node's
/// renderer. A kind the chart cannot draw shows an error, never a guess.
Widget renderLiveChartView(LiveChartViewNode node, NodeContext ctx) {
  final props = LiveChartProps.fromJson(ctx.childProps);
  if (!liveChartKinds.contains(props.kind)) {
    return Semantics(
      label: props.automationName ?? props.title,
      child: Text(
        'Unknown chart kind "${props.kind}" (${liveChartKinds.join(', ')})',
      ),
    );
  }
  return Semantics(
    container: true,
    explicitChildNodes: true,
    label:
        props.automationName ?? (props.title.isEmpty ? node.key : props.title),
    child: renderChart(
      ChartNode(
        chartKind: props.kind,
        title: props.title,
        points: node.points,
        comparePoints: null,
        seriesName: '',
        compareName: '',
        sourceNeuronUri: null,
        from: 'DigitalBrain',
        prefix: '',
        select: null,
      ),
      ctx,
    ),
  );
}
