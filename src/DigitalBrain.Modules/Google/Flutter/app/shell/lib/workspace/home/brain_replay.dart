import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter/material.dart';

final _appKey = RegExp(r'(?:^|/)apps/([^/]+)$');

BrainSnapshot emptyBrain() =>
    BrainSnapshot(rootId: 'replay', observedAt: DateTime.utc(1970));

String _neuronId(String key) {
  final app = _appKey.firstMatch(key);
  return app == null ? key : 'apps/${app.group(1)}';
}

BrainNeuron _neuron(String id, String type) => BrainNeuron(
  id: id,
  type: type,
  name: id,
  label: id.split('/').last,
  module: '',
);

BrainSnapshot _withNode(BrainSnapshot s, BrainNeuron node) => BrainSnapshot(
  rootId: s.rootId,
  observedAt: s.observedAt,
  nodes: [...s.nodes, node],
  synapses: s.synapses,
);

// The graph after one journalled signal, or null when the signal does not change it.
// Only signals that create or retire a neuron or an app move the replay; every other
// type is skipped, so a journal can carry types this shell has never heard of.
BrainSnapshot? growthStep(BrainSnapshot s, JournalSignal signal) {
  bool has(String id) => s.nodes.any((n) => n.id == id);
  switch (signal.type) {
    case 'InstanceCreated':
      final key = signal.field('key');
      if (key is! String || key.isEmpty) return null;
      final id = _neuronId(key);
      return has(id)
          ? null
          : _withNode(
              s,
              _neuron(id, signal.field('type') as String? ?? 'neuron'),
            );
    case 'AppChanged' || 'AppPublished' || 'AppReconciled':
      final name = signal.field('name');
      if (name is! String || name.isEmpty) return null;
      final id = 'apps/$name';
      return has(id) ? null : _withNode(s, _neuron(id, 'app'));
    case 'InstanceRetired':
      final key = signal.field('key');
      if (key is! String || !has(_neuronId(key))) return null;
      final id = _neuronId(key);
      return BrainSnapshot(
        rootId: s.rootId,
        observedAt: s.observedAt,
        nodes: [
          for (final n in s.nodes)
            if (n.id != id) n,
        ],
        synapses: [
          for (final e in s.synapses)
            if (e.sourceId != id && e.targetId != id) e,
        ],
      );
    default:
      return null;
  }
}

// The brain's growth read from sequence 0 in pages: frame i is the graph after i
// growth signals, frame 0 is the empty brain.
Future<List<BrainSnapshot>> loadGrowth(
  SignalsLoader load, {
  int pageSize = 200,
}) async {
  final frames = [emptyBrain()];
  var since = 0;
  while (true) {
    final page = await load(since, pageSize);
    if (page.signals.isEmpty) break;
    for (final signal in page.signals) {
      final next = growthStep(frames.last, signal);
      if (next != null) frames.add(next);
    }
    if (page.next <= since) break;
    since = page.next;
  }
  return frames;
}

// A scrub bar over the brain's journal. [onFrame] receives the graph to show while
// scrubbing and null when the live feed is back in charge (the end of the replay).
class BrainReplayBar extends StatefulWidget {
  const BrainReplayBar({
    super.key,
    required this.load,
    required this.onFrame,
    this.step = const Duration(milliseconds: 450),
  });

  final SignalsLoader load;
  final ValueChanged<BrainSnapshot?> onFrame;
  final Duration step;

  @override
  State<BrainReplayBar> createState() => _BrainReplayBarState();
}

class _BrainReplayBarState extends State<BrainReplayBar> {
  List<BrainSnapshot>? _frames;
  int _index = 0;
  bool _loading = false, _playing = false, _live = true;
  String? _error;
  Timer? _timer;

  int get _last => (_frames?.length ?? 1) - 1;

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  Future<void> _start() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final frames = await loadGrowth(widget.load);
      if (!mounted) return;
      setState(() {
        _frames = frames;
        _loading = false;
      });
      _play(from: 0);
    } catch (error) {
      if (mounted) {
        setState(() {
          _loading = false;
          _error = error.toString();
        });
      }
    }
  }

  void _halt() {
    _timer?.cancel();
    _timer = null;
  }

  void _goLive() {
    _halt();
    setState(() {
      _playing = false;
      _live = true;
      _index = _last;
    });
    widget.onFrame(null);
  }

  void _show(int index) {
    setState(() {
      _index = index;
      _live = false;
    });
    widget.onFrame(_frames![index]);
  }

  void _play({int? from}) {
    _halt();
    if (MediaQuery.disableAnimationsOf(context) || _last == 0) {
      _goLive();
      return;
    }
    setState(() => _playing = true);
    _show(from ?? (_index >= _last ? 0 : _index));
    _timer = Timer.periodic(widget.step, (_) {
      if (_index >= _last - 1) {
        _goLive();
      } else {
        _show(_index + 1);
      }
    });
  }

  void _pause() {
    _halt();
    setState(() => _playing = false);
  }

  void _scrub(double value) {
    _halt();
    _playing = false;
    final index = value.round();
    if (index >= _last) {
      _goLive();
    } else {
      _show(index);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_error != null) {
      return Text(
        _error!,
        key: const ValueKey('brain-replay-error'),
        style: TextStyle(color: Theme.of(context).colorScheme.error),
      );
    }
    if (_frames == null) {
      return Align(
        alignment: Alignment.centerLeft,
        child: _loading
            ? const SizedBox.square(
                dimension: 20,
                child: CircularProgressIndicator(strokeWidth: 2),
              )
            : TextButton.icon(
                key: const ValueKey('brain-replay-start'),
                onPressed: _start,
                icon: const Icon(Icons.history),
                label: const Text('Replay growth'),
              ),
      );
    }
    final last = _last;
    return Row(
      children: [
        IconButton(
          key: const ValueKey('brain-replay-play'),
          tooltip: _playing ? 'Pause' : 'Replay',
          icon: Icon(_playing ? Icons.pause : Icons.play_arrow),
          onPressed: _playing ? _pause : _play,
        ),
        Expanded(
          child: Slider(
            key: const ValueKey('brain-replay-scrub'),
            min: 0,
            max: last == 0 ? 1 : last.toDouble(),
            divisions: last == 0 ? null : last,
            value: last == 0 ? 0 : _index.toDouble(),
            onChanged: last == 0 ? null : _scrub,
          ),
        ),
        _live
            ? const Chip(
                key: ValueKey('brain-replay-is-live'),
                label: Text('Live'),
                visualDensity: VisualDensity.compact,
              )
            : TextButton(
                key: const ValueKey('brain-replay-live'),
                onPressed: _goLive,
                child: const Text('Back to live'),
              ),
      ],
    );
  }
}
