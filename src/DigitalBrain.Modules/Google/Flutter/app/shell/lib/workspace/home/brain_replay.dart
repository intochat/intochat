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

// One step of the brain's growth: a neuron or app appears, or a neuron retires.
final class GrowthOp {
  const GrowthOp.add(this.id, this.type) : adds = true;
  const GrowthOp.retire(this.id) : adds = false, type = '';
  final String id, type;
  final bool adds;
}

// The brain's growth as a compact list of steps. Only signals that create or retire a neuron
// or an app move it; every other type is skipped, so a journal can carry types this shell has
// never heard of. A frame is built from the steps when it is shown, never kept per step.
final class Growth {
  final _ops = <GrowthOp>[];
  final _ids = <String>{};

  // Frame 0 is the empty brain; frame i is the graph after i steps.
  int get frames => _ops.length + 1;

  void add(JournalSignal signal) {
    switch (signal.type) {
      case 'InstanceCreated':
        final key = signal.field('key');
        if (key is! String || key.isEmpty) return;
        final id = _neuronId(key);
        if (_ids.add(id)) {
          _ops.add(
            GrowthOp.add(id, signal.field('type') as String? ?? 'neuron'),
          );
        }
      case 'AppChanged' || 'AppPublished' || 'AppReconciled':
        final name = signal.field('name');
        if (name is! String || name.isEmpty) return;
        if (_ids.add('apps/$name')) _ops.add(GrowthOp.add('apps/$name', 'app'));
      case 'InstanceRetired':
        final key = signal.field('key');
        if (key is! String) return;
        final id = _neuronId(key);
        if (_ids.remove(id)) _ops.add(GrowthOp.retire(id));
      default:
    }
  }

  // The frame after step [index] (1-based), from the frame before it.
  BrainSnapshot step(BrainSnapshot previous, int index) {
    final op = _ops[index - 1];
    if (op.adds) {
      return _copy(previous, [
        ...previous.nodes,
        BrainNeuron(
          id: op.id,
          type: op.type,
          name: op.id,
          label: op.id.split('/').last,
          module: '',
        ),
      ], previous.synapses);
    }
    return _copy(
      previous,
      [
        for (final n in previous.nodes)
          if (n.id != op.id) n,
      ],
      [
        for (final e in previous.synapses)
          if (e.sourceId != op.id && e.targetId != op.id) e,
      ],
    );
  }

  BrainSnapshot frame(int index) {
    var snapshot = emptyBrain();
    for (var i = 1; i <= index; i++) {
      snapshot = step(snapshot, i);
    }
    return snapshot;
  }

  static BrainSnapshot _copy(
    BrainSnapshot s,
    List<BrainNeuron> nodes,
    List<BrainSynapse> synapses,
  ) => BrainSnapshot(
    rootId: s.rootId,
    observedAt: s.observedAt,
    nodes: nodes,
    synapses: synapses,
  );
}

// A scrub bar over the brain's journal. [onFrame] receives the graph to show while
// scrubbing and null when the live feed is back in charge (the end of the replay).
// Pages are read from sequence 0 as the replay plays; the first page starts it.
class BrainReplayBar extends StatefulWidget {
  const BrainReplayBar({
    super.key,
    required this.load,
    required this.onFrame,
    this.step = const Duration(milliseconds: 450),
    this.pageSize = 200,
  });

  final SignalsLoader load;
  final ValueChanged<BrainSnapshot?> onFrame;
  final Duration step;
  final int pageSize;

  @override
  State<BrainReplayBar> createState() => _BrainReplayBarState();
}

class _BrainReplayBarState extends State<BrainReplayBar> {
  final _growth = Growth();
  bool _started = false, _more = true;
  int _since = 0, _index = 0;
  bool _loading = false, _playing = false, _live = true;
  BrainSnapshot _current = emptyBrain();
  String? _error;
  Timer? _timer;

  int get _last => _growth.frames - 1;

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  // Reads pages until the journal ends. Every page that arrives is kept; a failed page leaves
  // what was loaded in place and offers a retry that resumes at the same sequence.
  Future<void> _read() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      while (_more) {
        final page = await widget.load(_since, widget.pageSize);
        if (!mounted) return;
        final first = !_started;
        setState(() {
          for (final signal in page.signals) {
            _growth.add(signal);
          }
          if (page.signals.isEmpty || page.next <= _since) {
            _more = false;
          } else {
            _since = page.next;
          }
          _started = true;
        });
        if (first) _play(from: 0);
      }
      if (!mounted) return;
      setState(() => _loading = false);
      if (_playing && MediaQuery.disableAnimationsOf(context)) _goLive();
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
      _current = _growth.frame(_last);
    });
    widget.onFrame(null);
  }

  void _show(int index) {
    setState(() {
      _current = index == _index + 1
          ? _growth.step(_current, index)
          : _growth.frame(index);
      _index = index;
      _live = false;
    });
    widget.onFrame(_current);
  }

  void _play({int? from}) {
    _halt();
    final reduced = MediaQuery.disableAnimationsOf(context);
    if (reduced && !_more) {
      _goLive();
      return;
    }
    setState(() => _playing = true);
    _show(from ?? (_index >= _last ? 0 : _index));
    _timer = Timer.periodic(widget.step, (_) {
      if (_index < _last) {
        _show(_index + 1);
      } else if (!_more) {
        _goLive();
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
    if (index >= _last && !_more) {
      _goLive();
    } else {
      _show(index);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (!_started) {
      return Align(
        alignment: Alignment.centerLeft,
        child: _loading
            ? const SizedBox.square(
                dimension: 20,
                child: CircularProgressIndicator(strokeWidth: 2),
              )
            : _error != null
            ? _retry(context)
            : TextButton.icon(
                key: const ValueKey('brain-replay-start'),
                onPressed: _read,
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
        if (_error != null) _retry(context),
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

  Widget _retry(BuildContext context) => TextButton.icon(
    key: const ValueKey('brain-replay-retry'),
    onPressed: _read,
    icon: const Icon(Icons.refresh),
    label: Text(
      'Retry',
      style: TextStyle(color: Theme.of(context).colorScheme.error),
    ),
  );
}
