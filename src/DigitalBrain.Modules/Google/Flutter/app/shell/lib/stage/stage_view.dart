import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'stage_controller.dart';

/// The stage when it has a home, otherwise [desktop], so an OS without a home scene is unchanged.
class StageHost extends StatelessWidget {
  const StageHost({
    super.key,
    required this.controller,
    required this.desktop,
    required this.client,
    this.ui,
    this.workspace = '',
    this.watchScene,
    this.watchBrain,
  });

  final StageController controller;
  final Widget desktop;
  final SceneClient client;
  final DigitalBrainUiClient? ui;
  final String workspace;
  final Stream<void> Function(String sceneKey)? watchScene;
  final WatchBrain? watchBrain;

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: controller,
    builder: (context, _) => controller.stage.home.isEmpty
        ? desktop
        : StageView(
            controller: controller,
            client: client,
            ui: ui,
            workspace: workspace,
            watchScene: watchScene,
            watchBrain: watchBrain,
          ),
  );
}

/// Renders the top stack entry: its scene full-bleed, or the scene at [dockedSceneWidth] beside
/// the docked application surface. Escape asks the stage to go home unless text is being edited.
class StageView extends StatefulWidget {
  const StageView({
    super.key,
    required this.controller,
    required this.client,
    this.ui,
    this.workspace = '',
    this.watchScene,
    this.watchBrain,
  });

  static const dockedSceneWidth = 320.0;

  final StageController controller;
  final SceneClient client;
  final DigitalBrainUiClient? ui;
  final String workspace;
  final Stream<void> Function(String sceneKey)? watchScene;
  final WatchBrain? watchBrain;

  @override
  State<StageView> createState() => _StageViewState();
}

class _StageViewState extends State<StageView> {
  final _refresh = <String, Stream<void>>{};

  Stream<void>? _refreshOf(String sceneKey) {
    final watch = widget.watchScene;
    if (watch == null) return null;
    return _refresh[sceneKey] ??= watch(sceneKey).asBroadcastStream();
  }

  @override
  void didUpdateWidget(StageView old) {
    super.didUpdateWidget(old);
    if (old.watchScene != widget.watchScene) _refresh.clear();
  }

  KeyEventResult _onKey(FocusNode node, KeyEvent event) {
    if (event is! KeyDownEvent ||
        event.logicalKey != LogicalKeyboardKey.escape ||
        _editing()) {
      return KeyEventResult.ignored;
    }
    unawaited(widget.controller.home());
    return KeyEventResult.handled;
  }

  static bool _editing() {
    final context = FocusManager.instance.primaryFocus?.context;
    if (context == null) return false;
    return context.widget is EditableText ||
        context.findAncestorWidgetOfExactType<EditableText>() != null;
  }

  // `{owner}/apps/{app}/surface` names the app whose surface is docked.
  static String _application(String surfaceKey) {
    final parts = surfaceKey.split('/');
    final apps = parts.lastIndexOf('apps');
    return apps >= 0 && apps + 1 < parts.length ? parts[apps + 1] : surfaceKey;
  }

  Widget _scene(StageEntry entry) => SceneView(
    key: ValueKey('scene:${entry.sceneKey}'),
    sceneKey: entry.sceneKey,
    client: widget.client,
    refresh: _refreshOf(entry.sceneKey),
    watchBrain: widget.watchBrain,
  );

  Widget _dock(String surfaceKey) {
    final ui = widget.ui;
    if (ui == null) {
      return const Center(child: Text('This surface is not available here.'));
    }
    return ApplicationSurface(
      key: ValueKey('dock:$surfaceKey'),
      client: ui,
      workspace: widget.workspace,
      application: _application(surfaceKey),
      start: () async => {'kind': 'surface', 'name': surfaceKey},
    );
  }

  Widget _entry(StageEntry? entry) {
    if (entry == null) return const SizedBox.expand(key: ValueKey('empty'));
    final dock = entry.dockSurfaceKey;
    if (dock == null) {
      return KeyedSubtree(key: ValueKey(entry.sceneKey), child: _scene(entry));
    }
    return Row(
      key: ValueKey('${entry.sceneKey}|$dock'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SizedBox(width: StageView.dockedSceneWidth, child: _scene(entry)),
        Expanded(child: _dock(dock)),
      ],
    );
  }

  @override
  Widget build(BuildContext context) {
    final stack = widget.controller.stage.stack;
    return Focus(
      autofocus: true,
      onKeyEvent: _onKey,
      child: AnimatedSwitcher(
        duration: const Duration(milliseconds: 300),
        child: _entry(stack.isEmpty ? null : stack.last),
      ),
    );
  }
}
