import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter/material.dart';

/// Micro-credits as dollars with up to six decimals: 1500 micros -> "$0.0015".
String computeDollars(int micros) {
  final dollars = micros / 1e6;
  return '\$${dollars.toStringAsFixed(6).replaceFirst(RegExp(r'\.?0+$'), '')}';
}

String computeTime(DateTime? value) {
  if (value == null) return 'Time unavailable';
  final local = value.toLocal();
  String two(int v) => v.toString().padLeft(2, '0');
  return '${local.year}-${two(local.month)}-${two(local.day)} ${two(local.hour)}:${two(local.minute)}';
}

class ComputeUsagePanel extends StatefulWidget {
  const ComputeUsagePanel({
    super.key,
    required this.client,
    required this.workspaceId,
    required this.workspaceName,
    required this.onClose,
    this.refreshSignal,
  });
  final IntoChatOSUiClient? client;
  final String workspaceId, workspaceName;
  final VoidCallback onClose;
  final Listenable? refreshSignal;
  @override
  State<ComputeUsagePanel> createState() => _ComputeUsagePanelState();
}

class _ComputeUsagePanelState extends State<ComputeUsagePanel> {
  ComputeSummary? _summary;
  List<ComputeCost> _recent = const [];
  String _grouping = 'app';
  String? _error;
  bool _busy = false, _pendingRefresh = false;
  int _generation = 0;
  Timer? _autoRefresh;

  @override
  void initState() {
    super.initState();
    widget.refreshSignal?.addListener(_refresh);
    // Cancellation can prevent the final receipt reaching chat. Recover committed
    // usage while this panel remains open, without depending on that event.
    _autoRefresh = Timer.periodic(
      const Duration(seconds: 30),
      (_) => _refresh(),
    );
    unawaited(_load());
  }

  void _refresh() => unawaited(_load());

  @override
  void didUpdateWidget(ComputeUsagePanel oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.refreshSignal != widget.refreshSignal) {
      oldWidget.refreshSignal?.removeListener(_refresh);
      widget.refreshSignal?.addListener(_refresh);
    }
    if (oldWidget.workspaceId != widget.workspaceId ||
        oldWidget.client != widget.client) {
      _generation++;
      _summary = null;
      _recent = const [];
      _grouping = 'app';
      _error = null;
      _busy = _pendingRefresh = false;
      unawaited(_load());
    }
  }

  @override
  void dispose() {
    _generation++;
    _autoRefresh?.cancel();
    widget.refreshSignal?.removeListener(_refresh);
    super.dispose();
  }

  Future<void> _load() async {
    final client = widget.client;
    if (client == null) return;
    if (_busy) {
      _pendingRefresh = true;
      return;
    }
    final generation = ++_generation;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final summary = await client.readCompute(
        widget.workspaceId,
        by: _grouping,
        since: DateTime.now().subtract(const Duration(days: 30)),
      );
      if (mounted && generation == _generation) {
        setState(() => _summary = summary);
      }
    } catch (_) {
      if (mounted && generation == _generation) {
        setState(
          () => _error =
              'Could not load usage. Displayed totals may be out of date.',
        );
      }
    }
    try {
      final recent = await client.readComputeRecent(widget.workspaceId);
      if (mounted && generation == _generation) {
        setState(() => _recent = recent);
      }
    } catch (_) {
      // Recent costs are supplemental; the grouped totals already carry the error state.
    }
    if (mounted && generation == _generation) {
      setState(() => _busy = false);
      if (_pendingRefresh) {
        _pendingRefresh = false;
        unawaited(_load());
      }
    }
  }

  Widget _overview(BuildContext context) {
    final summary = _summary;
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 4, 20, 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Brain account · ${widget.workspaceName}',
            style: Theme.of(context).textTheme.labelMedium,
          ),
          const SizedBox(height: 12),
          if (summary == null)
            if (_error == null)
              const Text('Loading compute…')
            else
              Text(_error!)
          else
            Wrap(
              spacing: 32,
              runSpacing: 12,
              children: [
                _amount(context, 'Balance', summary.balanceMicros),
                _amount(context, 'Spent · last 30 days', summary.spentMicros),
              ],
            ),
        ],
      ),
    );
  }

  Widget _amount(BuildContext context, String label, int micros) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(label, style: Theme.of(context).textTheme.labelMedium),
      Text(
        computeDollars(micros),
        style: Theme.of(context).textTheme.titleMedium,
      ),
    ],
  );

  Widget _groupingRow(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(20, 4, 20, 8),
    child: Wrap(
      spacing: 8,
      runSpacing: 8,
      children: [
        for (final group in const [
          ['origin', 'Origin'],
          ['app', 'App'],
          ['synapse', 'Synapse'],
          ['neuron', 'Neuron'],
          ['kind', 'Kind'],
        ])
          ChoiceChip(
            key: Key('compute-group-${group[0]}'),
            label: Text(group[1]),
            selected: _grouping == group[0],
            onSelected: (bool selected) {
              if (!selected || _grouping == group[0]) return;
              setState(() => _grouping = group[0]);
              unawaited(_load());
            },
          ),
      ],
    ),
  );

  Widget _breakdown(BuildContext context) {
    final summary = _summary;
    if (summary == null) return const SizedBox.shrink();
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 8, 20, 4),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            'Spend by $_grouping',
            style: Theme.of(context).textTheme.titleSmall,
          ),
          const SizedBox(height: 8),
          if (summary.rows.isEmpty)
            const Text('No usage recorded yet.')
          else
            for (final row in summary.rows)
              Row(
                children: [
                  Expanded(
                    child: Text(
                      row.key,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  Text(
                    '${row.count} calls',
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                  const SizedBox(width: 12),
                  Text(
                    computeDollars(row.micros),
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                ],
              ),
        ],
      ),
    );
  }

  Widget _recentSection(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(20, 8, 20, 12),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text('Recent', style: Theme.of(context).textTheme.titleSmall),
        const SizedBox(height: 8),
        if (_recent.isEmpty)
          const Text('No recent usage.')
        else
          for (final cost in _recent)
            Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '${cost.item} · ${cost.kind}${cost.synapse == null ? '' : ' · ${cost.synapse}'}',
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                      ),
                      Text(
                        computeTime(cost.at),
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    ],
                  ),
                ),
                Text(
                  computeDollars(cost.micros),
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ],
            ),
      ],
    ),
  );

  @override
  Widget build(BuildContext context) {
    final client = widget.client;
    final header = Padding(
      padding: const EdgeInsets.fromLTRB(12, 8, 8, 4),
      child: Row(
        children: [
          Expanded(
            child: Text(
              'Compute',
              style: Theme.of(context).textTheme.titleLarge,
            ),
          ),
          IconButton(
            tooltip: 'Refresh usage',
            onPressed: _busy ? null : _refresh,
            icon: const Icon(Icons.refresh),
          ),
          IconButton(
            tooltip: 'Close Compute',
            onPressed: widget.onClose,
            icon: const Icon(Icons.close),
          ),
        ],
      ),
    );
    if (client == null) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          header,
          const Expanded(
            child: Center(child: Text('Not connected to a brain')),
          ),
        ],
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        header,
        if (_busy) const LinearProgressIndicator(minHeight: 2),
        Expanded(
          child: SingleChildScrollView(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                _overview(context),
                _groupingRow(context),
                _breakdown(context),
                const Divider(height: 1),
                _recentSection(context),
              ],
            ),
          ),
        ),
      ],
    );
  }
}
