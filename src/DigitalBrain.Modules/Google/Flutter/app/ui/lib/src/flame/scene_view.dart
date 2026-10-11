import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flame/game.dart';
import 'package:flutter/material.dart';

import 'scene_game.dart';

/// Hosts one scene neuron in a [GameWidget].
///
/// With a [client] it reads the scene itself and re-reads on every [refresh] event (the
/// shell's watch stream for the scene key carries `SceneChanged`/`SceneEntered`); gestures go
/// back through [SceneClient.report]. Without one it draws [state] as given.
class SceneView extends StatefulWidget {
  const SceneView({
    super.key,
    required this.sceneKey,
    this.client,
    this.state,
    this.refresh,
  });

  final String sceneKey;
  final SceneClient? client;
  final SceneState? state;
  final Stream<void>? refresh;

  @override
  State<SceneView> createState() => _SceneViewState();
}

class _SceneViewState extends State<SceneView> {
  late final SceneGame _game = SceneGame()..onInput = _report;
  StreamSubscription<void>? _refresh;
  Object? _error;
  int? _revision;

  @override
  void initState() {
    super.initState();
    final state = widget.state;
    if (state != null) _show(state);
    _listen();
    unawaited(_read());
  }

  @override
  void didUpdateWidget(SceneView old) {
    super.didUpdateWidget(old);
    final state = widget.state;
    if (state != null && !identical(state, old.state)) _show(state);
    if (old.refresh != widget.refresh) _listen();
    if (old.sceneKey != widget.sceneKey || old.client != widget.client) {
      _revision = null;
      unawaited(_read());
    }
  }

  void _listen() {
    unawaited(_refresh?.cancel());
    _refresh = widget.refresh?.listen((_) => unawaited(_read()));
  }

  void _show(SceneState state) {
    _revision = state.revision;
    _game.apply(state);
  }

  Future<void> _read() async {
    final client = widget.client;
    if (client == null) return;
    try {
      final state = await client.read(widget.sceneKey);
      if (!mounted) return;
      final hadError = _error != null;
      _error = null;
      if (state.revision != _revision) _show(state);
      if (hadError) setState(() {});
    } catch (error) {
      if (mounted) setState(() => _error = error);
    }
  }

  void _report(
    String nodeId,
    String kind,
    double x,
    double y,
    String? payload,
  ) {
    final client = widget.client;
    if (client == null) return;
    unawaited(
      client
          .report(widget.sceneKey, nodeId, kind, x, y, payload: payload)
          .catchError((Object _) {}),
    );
  }

  @override
  void dispose() {
    unawaited(_refresh?.cancel());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Stack(
    fit: StackFit.expand,
    children: [
      GameWidget<SceneGame>(game: _game),
      if (_error != null)
        Align(
          alignment: Alignment.bottomCenter,
          child: Padding(
            padding: const EdgeInsets.all(8),
            child: Text(
              'This scene could not be read.',
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ),
        ),
    ],
  );
}
