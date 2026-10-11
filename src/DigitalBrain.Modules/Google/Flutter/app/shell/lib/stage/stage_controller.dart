import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter/foundation.dart';

/// The live-signal feed of one brain for the given simple signal type names
/// (`DigitalBrainUiClient.watchLive` bound to the brain).
typedef WatchSignals = Stream<LiveEnvelope> Function(List<String> types);

/// The person's stage: the scene stack the shell renders. The stage key is the server's
/// (`GET ui/stage` resolves the caller's own stage); `StageChanged` is followed for that key.
class StageController extends ChangeNotifier {
  StageController({required this.client, this.live});

  StageController.fake(StageState state, {this.client})
    : live = null,
      _stage = state;

  final SceneClient? client;
  final WatchSignals? live;
  StageState _stage = const StageState(name: '', revision: 0);
  StreamSubscription<LiveEnvelope>? _changes;
  String? _watched;
  bool _disposed = false;

  StageState get stage => _stage;

  /// Reads the stage, drops entries whose scene is gone (404), and follows `StageChanged`.
  Future<void> refresh() async {
    final client = this.client;
    if (client == null) return;
    StageState state;
    try {
      state = await client.stage();
    } catch (_) {
      return;
    }
    final kept = <StageEntry>[];
    for (final entry in state.stack) {
      try {
        await client.read(entry.sceneKey);
        kept.add(entry);
      } on UiRequestException catch (error) {
        if (error.statusCode != 404) kept.add(entry);
      } catch (_) {
        kept.add(entry);
      }
    }
    if (_disposed) return;
    _stage = kept.length == state.stack.length
        ? state
        : StageState(
            name: state.name,
            revision: state.revision,
            home: state.home,
            stack: kept,
          );
    _follow(state.name);
    notifyListeners();
  }

  void _follow(String name) {
    final live = this.live;
    if (live == null || name.isEmpty || name == _watched) return;
    _watched = name;
    unawaited(_changes?.cancel());
    _changes = live(const ['StageChanged'])
        .where(
          (envelope) =>
              !envelope.isReady &&
              (envelope.payload['name'] ?? envelope.payload['Name']) == name,
        )
        .listen((_) => unawaited(refresh()), onError: (Object _) {});
  }

  Future<void> go(String sceneKey, {String? dockSurfaceKey}) =>
      _then(client?.go(sceneKey, dockSurfaceKey: dockSurfaceKey));

  Future<void> back() => _then(client?.back());

  Future<void> home() => _then(client?.home());

  Future<void> _then(Future<void>? call) async {
    if (call == null) return;
    try {
      await call;
    } catch (_) {
      return;
    }
    await refresh();
  }

  @override
  void dispose() {
    _disposed = true;
    unawaited(_changes?.cancel());
    super.dispose();
  }
}
