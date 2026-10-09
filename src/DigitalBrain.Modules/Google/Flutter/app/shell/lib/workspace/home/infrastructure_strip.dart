import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter/material.dart';

enum InfrastructureHealth { green, amber, red }

// Aspire health decides first, then state. A connection row carries its status as the state.
InfrastructureHealth infrastructureHealth(String state, String? health) {
  if (health == 'Healthy' || state == 'Connected') return InfrastructureHealth.green;
  if (health == 'Unhealthy' ||
      state == 'Exited' ||
      state == 'FailedToStart' ||
      state == 'Stopped' ||
      state == 'Disconnected' ||
      state == 'Failing') {
    return InfrastructureHealth.red;
  }
  return InfrastructureHealth.amber;
}

Color infrastructureColor(InfrastructureHealth health) => switch (health) {
  InfrastructureHealth.green => Colors.green,
  InfrastructureHealth.amber => Colors.amber,
  InfrastructureHealth.red => Colors.red,
};

// One chip per module resource and per user connection, colored by health, re-read on an
// interval: the registry's state changes under us (ResourceStateChanged) and the shell has no
// signal stream for it, so the strip reads like ComputeUsagePanel does.
class InfrastructureStrip extends StatefulWidget {
  const InfrastructureStrip({
    super.key,
    required this.load,
    this.loadConnections,
    this.onTap,
    this.refreshEvery = const Duration(seconds: 5),
  });

  final Future<List<InfrastructureRow>> Function() load;
  final Future<Object?> Function()? loadConnections;
  final ValueChanged<String>? onTap;
  final Duration refreshEvery;

  @override
  State<InfrastructureStrip> createState() => _InfrastructureStripState();
}

class _InfrastructureStripState extends State<InfrastructureStrip> {
  List<InfrastructureRow> _rows = const [];
  String? _error;
  Timer? _refresh;

  @override
  void initState() {
    super.initState();
    unawaited(_load());
    _refresh = Timer.periodic(widget.refreshEvery, (_) => unawaited(_load()));
  }

  @override
  void dispose() {
    _refresh?.cancel();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final rows = await widget.load();
      final connections = await widget.loadConnections?.call();
      if (!mounted) return;
      setState(() {
        _rows = [
          ...rows,
          for (final item in connections as List? ?? const [])
            InfrastructureRow(
              module: '${(item as Map)['connectorId']}',
              resource: 'connection ${item['connectionId']}',
              state: '${item['status']}',
            ),
        ];
        _error = null;
      });
    } catch (error) {
      if (mounted) setState(() => _error = error.toString());
    }
  }

  @override
  Widget build(BuildContext context) => Wrap(
    spacing: 12,
    runSpacing: 12,
    children: [
      if (_error != null)
        Text(
          _error!,
          key: const ValueKey('infrastructure-error'),
          style: TextStyle(color: Theme.of(context).colorScheme.error),
        ),
      for (final row in _rows)
        ActionChip(
          key: ValueKey('infra-${row.module}-${row.resource}'),
          avatar: CircleAvatar(
            key: ValueKey('infra-dot-${row.module}-${row.resource}'),
            radius: 5,
            backgroundColor: infrastructureColor(
              infrastructureHealth(row.state, row.health),
            ),
          ),
          label: Text(row.module),
          tooltip:
              '${row.resource}: ${row.state}${row.health == null ? '' : ' (${row.health})'}',
          onPressed: widget.onTap == null ? null : () => widget.onTap!(row.module),
        ),
    ],
  );
}