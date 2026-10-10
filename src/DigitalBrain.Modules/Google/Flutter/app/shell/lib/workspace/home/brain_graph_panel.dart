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
  });

  final Stream<BrainSnapshot> Function() watch;
  final SignalsLoader? loadSignals;
  final double height;

  @override
  State<BrainGraphPanel> createState() => _BrainGraphPanelState();
}

class _BrainGraphPanelState extends State<BrainGraphPanel> {
  BrainSnapshot? _live, _replay;
  Object? _error;
  String? _selected;
  StreamSubscription<BrainSnapshot>? _subscription;

  @override
  void initState() {
    super.initState();
    _subscription = widget.watch().listen(
      (snapshot) => setState(() {
        _live = snapshot;
        _error = null;
      }),
      onError: (Object error) => setState(() => _error = error),
    );
  }

  @override
  void dispose() {
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
