import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter/material.dart';

enum InfrastructureHealth { green, amber, red }

// Aspire health decides first, then state. A connection row carries its status as the state.
InfrastructureHealth infrastructureHealth(String state, String? health) {
  if (health == 'Healthy' || state == 'Connected') {
    return InfrastructureHealth.green;
  }
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

// One chip per silo, colored by health. The strip listens to the cluster's own signal: a silo changing
// state animates its chip, a silo that leaves the cluster loses it, nothing is polled.
class InfrastructureStrip extends StatefulWidget {
  const InfrastructureStrip({
    super.key,
    this.watch,
    this.onTap,
    @Deprecated('The strip no longer reads infrastructure rows; pass watch.')
    this.load,
    @Deprecated('The strip no longer lists connections.') this.loadConnections,
  });

  final Stream<ClusterChanged> Function()? watch;
  final ValueChanged<String>? onTap;
  final Object? Function()? load;
  final Object? Function()? loadConnections;

  @override
  State<InfrastructureStrip> createState() => _InfrastructureStripState();
}

class _InfrastructureStripState extends State<InfrastructureStrip> {
  ClusterChanged? _cluster;
  String? _error;
  StreamSubscription<ClusterChanged>? _subscription;

  @override
  void initState() {
    super.initState();
    _subscription = widget.watch?.call().listen(
      (cluster) => setState(() {
        _cluster = cluster;
        _error = null;
      }),
      onError: (Object error) => setState(() => _error = error.toString()),
    );
  }

  @override
  void dispose() {
    unawaited(_subscription?.cancel());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final motion = MediaQuery.of(context).disableAnimations
        ? Duration.zero
        : const Duration(milliseconds: 300);
    final cluster = _cluster;
    return Wrap(
      spacing: 12,
      runSpacing: 12,
      children: [
        if (_error != null)
          Text(
            _error!,
            key: const ValueKey('infrastructure-error'),
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
        if (cluster != null && cluster.stage != 'steady')
          Chip(
            key: const ValueKey('cluster-stage'),
            label: Text('${cluster.stage} ${_short(cluster.sha)}'.trim()),
          ),
        for (final silo in cluster?.silos ?? const <SiloState>[])
          ActionChip(
            key: ValueKey('silo-${silo.id}'),
            avatar: AnimatedSwitcher(
              duration: motion,
              child: CircleAvatar(
                key: ValueKey(
                  'silo-dot-${silo.id}-${silo.state}-${silo.health}',
                ),
                radius: 5,
                backgroundColor: infrastructureColor(
                  infrastructureHealth(silo.state, silo.health),
                ),
              ),
            ),
            label: Text(silo.id),
            tooltip:
                '${silo.state}${silo.health == null ? '' : ' (${silo.health})'} ${_short(silo.sha)}'
                    .trim(),
            onPressed: widget.onTap == null
                ? null
                : () => widget.onTap!(silo.id),
          ),
      ],
    );
  }

  static String _short(String sha) =>
      sha.length > 7 ? sha.substring(0, 7) : sha;
}
