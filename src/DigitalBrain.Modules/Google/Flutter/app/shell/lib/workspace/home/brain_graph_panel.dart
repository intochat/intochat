import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';

import 'brain_replay.dart';

// Home's brain: the live snapshot feed drawn as a graph that animates what changed between two
// snapshots, with a scrub bar that replays the brain's growth and then hands back to the feed.
class BrainGraphPanel extends StatefulWidget {
  const BrainGraphPanel({
    super.key,
    required this.watch,
    this.loadSignals,
    this.height = 360,
    this.retryAfter = const Duration(seconds: 1),
    this.retryCeiling = const Duration(seconds: 30),
    this.stableAfter = const Duration(seconds: 5),
  });

  final Stream<BrainSnapshot> Function() watch;
  final SignalsLoader? loadSignals;
  final double height;
  // A feed that stays open this long counts as healthy: only then does the retry pause start over.
  final Duration retryAfter, retryCeiling, stableAfter;

  @override
  State<BrainGraphPanel> createState() => _BrainGraphPanelState();
}

class _BrainGraphPanelState extends State<BrainGraphPanel> {
  BrainSnapshot? _live, _replay;
  Object? _error;
  String? _selected;
  StreamSubscription<BrainSnapshot>? _subscription;

  Timer? _retry, _stable;
  late Duration _pause = widget.retryAfter;

  @override
  void initState() {
    super.initState();
    _connect();
  }

  // A feed that ends or fails keeps the last graph, marked stale, and is reopened with a growing pause;
  // the reopened feed starts with the whole brain again.
  void _connect() {
    unawaited(_subscription?.cancel());
    _stable?.cancel();
    _stable = Timer(widget.stableAfter, () => _pause = widget.retryAfter);
    _subscription = widget.watch().listen(
      (snapshot) => setState(() {
        _live = snapshot;
        _error = null;
      }),
      onError: _lost,
      onDone: () => _lost(StateError('The brain feed ended.')),
      cancelOnError: true,
    );
  }

  void _lost(Object error) {
    _stable?.cancel();
    if (!mounted) return;
    if (_error == null) setState(() => _error = error);
    _retry?.cancel();
    _retry = Timer(_pause, _connect);
    final doubled = _pause * 2;
    _pause = doubled > widget.retryCeiling ? widget.retryCeiling : doubled;
  }

  @override
  void dispose() {
    _retry?.cancel();
    _stable?.cancel();
    unawaited(_subscription?.cancel());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final shown = _replay ?? _live;
    final loadSignals = widget.loadSignals;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SizedBox(
          height: widget.height,
          child: shown == null
              ? Center(
                  child: _error == null
                      ? const CircularProgressIndicator()
                      : Text(
                          'The brain is unreachable.',
                          key: const ValueKey('brain-graph-error'),
                          style: TextStyle(
                            color: Theme.of(context).colorScheme.error,
                          ),
                        ),
                )
              : ClipRect(
                  child: LumenBrainGraph(
                    key: ValueKey(
                      _replay == null ? 'brain-graph' : 'brain-graph-replay',
                    ),
                    snapshot: shown,
                    stale: _error != null && _replay == null,
                    selectedId: _selected,
                    onNeuron: (node) => setState(
                      () => _selected = _selected == node.id ? null : node.id,
                    ),
                    onSynapse: (edge) => setState(
                      () => _selected = _selected == edge.id ? null : edge.id,
                    ),
                  ),
                ),
        ),
        if (loadSignals != null)
          BrainReplayBar(
            load: loadSignals,
            onFrame: (frame) => setState(() => _replay = frame),
          ),
      ],
    );
  }
}
