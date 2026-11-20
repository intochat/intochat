import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../design/cards/chart_card.dart';
import '../../design/tokens.dart';
import '../../nodes/node_common.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Chart.IChart`.
class ChartWidget extends StatelessWidget {
  const ChartWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  static ChartKind _parseKind(String? raw) => switch (raw?.toLowerCase()) {
    'line' => ChartKind.line,
    'area' => ChartKind.area,
    'pie' || 'donut' => ChartKind.pie,
    _ => ChartKind.columns,
  };

  static ChartState _parseStatus(String? raw, bool hasPoints) =>
      switch (raw?.toLowerCase()) {
        'loading' => ChartState.loading,
        'error' => ChartState.error,
        'empty' => ChartState.empty,
        _ => hasPoints ? ChartState.ready : ChartState.empty,
      };

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.chart',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = '${state['title'] ?? 'Chart'}';
        final description = '${state['description'] ?? ''}'.trim();
        final from = '${state['from'] ?? 'DigitalBrain'}';
        final prefix = '${state['prefix'] ?? ''}';
        final error = state['error'] as String?;
        final kind = _parseKind(state['kind'] as String?);
        final rawSeries = state['series'] is List
            ? state['series'] as List
            : const <dynamic>[];

        final points = <ChartPoint>[];
        var primaryName = '';
        List<double>? compare;
        var compareName = '';

        if (rawSeries.isNotEmpty && rawSeries[0] is Map) {
          final first = rawSeries[0] as Map;
          primaryName = '${first['name'] ?? ''}';
          final rawPts = first['points'] is List
              ? first['points'] as List
              : const <dynamic>[];
          for (final p in rawPts) {
            if (p is Map) {
              points.add(
                ChartPoint(
                  '${p['label'] ?? ''}',
                  (p['value'] as num?)?.toDouble() ?? 0.0,
                ),
              );
            }
          }
        }
        if (rawSeries.length > 1 && rawSeries[1] is Map) {
          final second = rawSeries[1] as Map;
          compareName = '${second['name'] ?? ''}';
          final rawPts = second['points'] is List
              ? second['points'] as List
              : const <dynamic>[];
          compare = [
            for (final p in rawPts)
              if (p is Map) (p['value'] as num?)?.toDouble() ?? 0.0,
          ];
        }

        final chartState = _parseStatus(
          state['status'] as String?,
          points.isNotEmpty,
        );
        final selected = state['selectedPoint'] is Map
            ? state['selectedPoint'] as Map
            : null;
        final uri = 'neuron://$neuronKey';

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            ChartCard(
              source: cardSource(from),
              state: chartState,
              kind: kind,
              data: ChartData(
                title: title,
                points: points,
                from: from,
                prefix: prefix,
                compare: compare,
                seriesName: primaryName,
                compareName: compareName,
                sourceNeuronUri: uri,
                errorText:
                    error ?? 'Something stopped the lookup. Nothing changed.',
              ),
              onSwitchChartKind: (nextKind) =>
                  unawaited(invoke('SetKind', [nextKind.name])),
              onSelectPoint: (index) {
                if (index >= 0 && index < points.length) {
                  unawaited(
                    invoke('SelectPoint', [points[index].label, primaryName]),
                  );
                }
              },
              onRetry: () => unawaited(invoke('Refresh')),
              onCopyNeuronUri: (u) =>
                  unawaited(Clipboard.setData(ClipboardData(text: u))),
            ),
            if (description.isNotEmpty || selected != null)
              Padding(
                padding: const EdgeInsets.only(top: 6, left: 4, right: 4),
                child: Wrap(
                  spacing: 12,
                  runSpacing: 4,
                  crossAxisAlignment: WrapCrossAlignment.center,
                  children: [
                    if (description.isNotEmpty)
                      Text(
                        description,
                        style: TextStyle(fontSize: 12, color: c.textMuted),
                      ),
                    if (selected != null)
                      Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 8,
                          vertical: 3,
                        ),
                        decoration: BoxDecoration(
                          color: c.accent.withValues(alpha: 0.12),
                          borderRadius: BorderRadius.circular(999),
                        ),
                        child: Text(
                          'Selected: ${selected['label']} (${selected['seriesName']}: $prefix${selected['value']})',
                          style: TextStyle(
                            fontSize: 11,
                            fontWeight: FontWeight.w600,
                            color: c.accent,
                            fontFeatures: const [FontFeature.tabularFigures()],
                          ),
                        ),
                      ),
                  ],
                ),
              ),
          ],
        );
      },
    );
  }
}
