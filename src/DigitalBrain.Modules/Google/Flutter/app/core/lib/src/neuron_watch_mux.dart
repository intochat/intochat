import 'dart:async';

import 'neuron_channel.dart';

/// Carries every node watch of one brain on as few `Watch` streams as the server allows
/// ([maxSourceSignals] source signals per stream, `NeuronsService.MaximumWatches`).
///
/// Adding or removing a source reopens the shared stream after [debounce]; a stream that ends or
/// breaks is reopened after [retryAfter]. The server replays each source's retained last signal on
/// every open, so a replay already delivered on an earlier open is dropped and a reopen does not
/// make every view read again.
final class NeuronWatchMux {
  NeuronWatchMux(
    this._link,
    this.brainId, {
    this.debounce = const Duration(milliseconds: 50),
    this.retryAfter = const Duration(seconds: 2),
  });

  static const maxSourceSignals = 64;

  final NeuronLink Function() _link;
  final String brainId;
  final Duration debounce, retryAfter;
  final _sources = <String, _Source>{};
  final _replayed = <String, int>{};
  final _streams = <StreamSubscription<NeuronSignal>>[];
  Timer? _reopen;
  bool _closed = false;

  /// How many `Watch` streams are open; a screen's watches share them.
  int get openStreams => _streams.length;

  /// The source's signals for as long as the returned stream is listened to.
  Stream<NeuronSignal> watch(WatchSourceSpec spec) {
    late final StreamController<NeuronSignal> controller;
    controller = StreamController<NeuronSignal>(
      onListen: () => _add(spec, controller),
      onCancel: () => _remove(spec, controller),
    );
    return controller.stream;
  }

  void _add(WatchSourceSpec spec, StreamController<NeuronSignal> listener) {
    final id = _id(spec);
    final source = _sources[id];
    if (source != null) {
      source.listeners.add(listener);
      return;
    }
    _sources[id] = _Source(spec)..listeners.add(listener);
    _schedule(debounce);
  }

  void _remove(WatchSourceSpec spec, StreamController<NeuronSignal> listener) {
    final id = _id(spec);
    final source = _sources[id];
    if (source == null) return;
    source.listeners.remove(listener);
    if (source.listeners.isNotEmpty) return;
    _sources.remove(id);
    _replayed.removeWhere((key, _) => key.startsWith('${spec.key}\u0000'));
    _schedule(debounce);
  }

  void _schedule(Duration after) {
    if (_closed) return;
    _reopen?.cancel();
    _reopen = Timer(after, _open);
  }

  void _open() {
    _reopen = null;
    for (final stream in _streams) {
      unawaited(stream.cancel());
    }
    _streams.clear();
    if (_closed || _sources.isEmpty) return;
    final link = _link();
    for (final chunk in _chunks()) {
      late final StreamSubscription<NeuronSignal> subscription;
      subscription = link
          .watch(brainId, chunk)
          .listen(
            _deliver,
            onError: (Object _) => _broken(subscription),
            onDone: () => _broken(subscription),
            cancelOnError: true,
          );
      _streams.add(subscription);
    }
  }

  void _broken(StreamSubscription<NeuronSignal> subscription) {
    if (!_streams.contains(subscription) || _reopen != null) return;
    _schedule(retryAfter);
  }

  // Each source counts once per signal type, as the server counts it.
  List<List<WatchSourceSpec>> _chunks() {
    final chunks = <List<WatchSourceSpec>>[];
    var current = <WatchSourceSpec>[];
    var weight = 0;
    for (final source in _sources.values) {
      final cost = source.spec.types.isEmpty ? 1 : source.spec.types.length;
      if (weight + cost > maxSourceSignals && current.isNotEmpty) {
        chunks.add(current);
        current = [];
        weight = 0;
      }
      current.add(source.spec);
      weight += cost;
    }
    if (current.isNotEmpty) chunks.add(current);
    return chunks;
  }

  void _deliver(NeuronSignal signal) {
    // Live signals carry sequence 0; a retained replay carries its journal sequence.
    if (signal.sequence > 0) {
      final key = '${signal.source}\u0000${signal.type}';
      if (_replayed[key] == signal.sequence) return;
      _replayed[key] = signal.sequence;
    }
    for (final source in _sources.values) {
      if (source.spec.key != signal.source) continue;
      if (source.spec.types.isNotEmpty &&
          !source.spec.types.contains(signal.type)) {
        continue;
      }
      for (final listener in source.listeners.toList()) {
        listener.add(signal);
      }
    }
  }

  /// Ends every stream; listeners receive done.
  Future<void> close() async {
    _closed = true;
    _reopen?.cancel();
    for (final stream in _streams) {
      unawaited(stream.cancel());
    }
    _streams.clear();
    final listeners = [for (final s in _sources.values) ...s.listeners];
    _sources.clear();
    for (final listener in listeners) {
      unawaited(listener.close());
    }
  }

  static String _id(WatchSourceSpec spec) =>
      '${spec.contract}\u0000${spec.key}\u0000${spec.types.join(',')}';
}

final class _Source {
  _Source(this.spec);
  final WatchSourceSpec spec;
  final listeners = <StreamController<NeuronSignal>>{};
}
