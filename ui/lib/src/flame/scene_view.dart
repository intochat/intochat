import 'dart:async';
import 'dart:math' as math;

import 'package:flame/game.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart' hide Focus;
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'prefabs/sphere.dart';
import 'scene_game.dart';

typedef SceneReplayKeyHandler = void Function(String key, String cursor);

/// Hosts one scene neuron in a [GameWidget] with interactive camera pan/zoom/fit/reset,
/// `[● Follow Live]` smooth camera tracking, and a `132×72px` corner Viewport Minimap.
class SceneView extends StatefulWidget {
  const SceneView({
    super.key,
    required this.sceneKey,
    this.client,
    this.state,
    this.refresh,
    this.animateChanges = true,
    this.onInput,
    this.loadIcon,
    this.game,
    this.showCameraHud = true,
    this.onReplayKey,
  });

  final String sceneKey;
  final SceneClient? client;
  final SceneState? state;
  final Stream<void>? refresh;

  final bool animateChanges;
  final SceneInputHandler? onInput;
  final SphereIconLoader? loadIcon;

  /// The game to draw into, for a host that adds components of its own (the brain scene's pulses); a fresh one by default.
  final SceneGame? game;

  /// Whether to render the floating camera HUD & corner Viewport Minimap.
  final bool showCameraHud;

  /// Optional callback invoked on time-travel shortcuts (`ArrowLeft`, `ArrowRight`, `Space`, `Home`, `End`).
  final SceneReplayKeyHandler? onReplayKey;

  @override
  State<SceneView> createState() => _SceneViewState();
}

final class _TrackedPointer {
  _TrackedPointer({
    required this.startPos,
    required this.buttons,
    required this.overDraggable,
  }) : lastPos = startPos;

  final Offset startPos;
  final int buttons;
  final bool overDraggable;
  Offset lastPos;
  bool panning = false;
}

class _SceneViewState extends State<SceneView> {
  static KeyEvent? _lastDispatchedReplayKey;

  late final SceneGame _game = (widget.game ?? SceneGame())
    ..onInput = _report
    ..loadIcon = widget.loadIcon;
  final FocusNode _focusNode = FocusNode(debugLabel: 'SceneView');
  final Map<int, _TrackedPointer> _pointers = {};
  double _lastPanZoomScale = 1.0;
  Size _viewportSize = const Size(800, 500);
  StreamSubscription<void>? _refresh;
  Object? _error;
  int? _revision;

  @override
  void initState() {
    super.initState();
    HardwareKeyboard.instance.addHandler(_onHardwareKey);
    final state = widget.state;
    if (state != null) _show(state);
    _listen();
    if (state == null) unawaited(_read());
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final animations = !MediaQuery.disableAnimationsOf(context);
    _game.appearanceDuration = UiTokens.of(context).standard;
    if (_game.animations != animations) {
      _game
        ..animations = animations
        ..host();
    }
  }

  @override
  void didUpdateWidget(SceneView old) {
    super.didUpdateWidget(old);
    if (old.loadIcon != widget.loadIcon) {
      _game
        ..loadIcon = widget.loadIcon
        ..host();
    }
    final state = widget.state;
    if (state != null && !identical(state, old.state)) _show(state);
    if (old.refresh != widget.refresh) _listen();
    if (old.sceneKey != widget.sceneKey || old.client != widget.client) {
      _revision = null;
      if (state == null) unawaited(_read());
    }
  }

  void _listen() {
    unawaited(_refresh?.cancel());
    _refresh = widget.refresh?.listen((_) => unawaited(_read()));
  }

  void _show(SceneState state) {
    _revision = state.revision;
    final animations = _game.animations;
    if (!widget.animateChanges) _game.animations = false;
    _game.apply(state);
    _game.animations = animations;
    _game.host();
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
    if (widget.onInput != null) {
      widget.onInput!(nodeId, kind, x, y, payload);
      return;
    }
    final client = widget.client;
    if (client == null) return;
    unawaited(
      client
          .report(widget.sceneKey, nodeId, kind, x, y, payload: payload)
          .catchError((Object _) {}),
    );
  }

  bool _isTextInputFocused() {
    final focus = FocusManager.instance.primaryFocus;
    final ctx = focus?.context;
    if (ctx == null) return false;
    if (ctx.widget is EditableText) return true;
    if (ctx is StatefulElement && ctx.state is TextInputClient) return true;
    var found = false;
    ctx.visitAncestorElements((element) {
      if (element.widget is EditableText ||
          (element is StatefulElement && element.state is TextInputClient)) {
        found = true;
        return false;
      }
      return true;
    });
    return found;
  }

  bool _isInteractiveControlFocused() {
    final focus = FocusManager.instance.primaryFocus;
    if (focus == null || identical(focus, _focusNode)) return false;
    final ctx = focus.context;
    if (ctx == null) return false;
    if (ctx.widget is FocusScope) return false;
    var interactive = false;
    ctx.visitAncestorElements((element) {
      final w = element.widget;
      if (w is ButtonStyleButton || w is IconButton || w is InkWell) {
        interactive = true;
        return false;
      }
      return true;
    });
    return interactive;
  }

  bool _onHardwareKey(KeyEvent event) {
    if (!mounted) return false;
    if (event is! KeyDownEvent && event is! KeyRepeatEvent) return false;
    final route = ModalRoute.of(context);
    if (route != null && !route.isCurrent) return false;
    if (_isTextInputFocused()) return false;

    final hw = HardwareKeyboard.instance;
    if (hw.isControlPressed || hw.isMetaPressed || hw.isAltPressed) {
      return false;
    }

    final key = event.logicalKey;
    if (key == LogicalKeyboardKey.keyW) {
      _game.panCameraDirection(0, -1);
      return true;
    }
    if (key == LogicalKeyboardKey.keyS) {
      _game.panCameraDirection(0, 1);
      return true;
    }
    if (key == LogicalKeyboardKey.keyA) {
      _game.panCameraDirection(-1, 0);
      return true;
    }
    if (key == LogicalKeyboardKey.keyD) {
      _game.panCameraDirection(1, 0);
      return true;
    }

    final onReplayKey = widget.onReplayKey;
    if (onReplayKey == null || identical(event, _lastDispatchedReplayKey)) {
      return false;
    }

    if (key == LogicalKeyboardKey.arrowLeft) {
      _lastDispatchedReplayKey = event;
      onReplayKey('replay-prev', 'prev');
      return true;
    }
    if (key == LogicalKeyboardKey.arrowRight) {
      _lastDispatchedReplayKey = event;
      onReplayKey('replay-next', 'next');
      return true;
    }
    if (event is KeyDownEvent && key == LogicalKeyboardKey.home) {
      _lastDispatchedReplayKey = event;
      onReplayKey('replay-first', 'first');
      return true;
    }
    if (event is KeyDownEvent && key == LogicalKeyboardKey.end) {
      _lastDispatchedReplayKey = event;
      onReplayKey('replay-last', 'last');
      return true;
    }
    if (event is KeyDownEvent &&
        key == LogicalKeyboardKey.space &&
        !_isInteractiveControlFocused()) {
      _lastDispatchedReplayKey = event;
      onReplayKey('replay-toggle', 'toggle');
      return true;
    }
    return false;
  }

  void _onPointerDown(PointerDownEvent event) {
    if (!_focusNode.hasFocus) {
      _focusNode.requestFocus();
    }
    final isMiddle = (event.buttons & kMiddleMouseButton) != 0;
    final overDraggable =
        !isMiddle &&
        _game.hasDraggableAt(event.localPosition, viewportSize: _viewportSize);
    _pointers[event.pointer] = _TrackedPointer(
      startPos: event.localPosition,
      buttons: event.buttons,
      overDraggable: overDraggable,
    );
  }

  void _onPointerMove(PointerMoveEvent event) {
    final tracked = _pointers[event.pointer];
    if (tracked == null) return;

    if (_pointers.length >= 2) {
      final pair = _pointers.entries.take(2).toList(growable: false);
      if (pair[0].key == event.pointer || pair[1].key == event.pointer) {
        final p1 = pair[0].value;
        final p2 = pair[1].value;
        final oldP1 = p1.lastPos;
        final oldP2 = p2.lastPos;
        tracked.lastPos = event.localPosition;
        p1.panning = true;
        p2.panning = true;
        final newP1 = p1.lastPos;
        final newP2 = p2.lastPos;
        final oldMid = Offset(
          (oldP1.dx + oldP2.dx) / 2,
          (oldP1.dy + oldP2.dy) / 2,
        );
        final newMid = Offset(
          (newP1.dx + newP2.dx) / 2,
          (newP1.dy + newP2.dy) / 2,
        );
        if (newMid != oldMid) {
          _game.panByScreenDelta(newMid - oldMid);
        }
        final oldDist = (oldP1 - oldP2).distance;
        final newDist = (newP1 - newP2).distance;
        if (oldDist > 4.0 && newDist > 4.0) {
          _game.zoomBy(
            newDist / oldDist,
            focalPoint: newMid,
            viewportSize: _viewportSize,
          );
        }
      } else {
        tracked.lastPos = event.localPosition;
      }
      return;
    }

    tracked.lastPos = event.localPosition;
    if (tracked.overDraggable) return;

    final isMiddle =
        (event.buttons & kMiddleMouseButton) != 0 ||
        (tracked.buttons & kMiddleMouseButton) != 0;
    final isPrimaryOrTouch =
        (event.buttons & kPrimaryButton) != 0 ||
        event.kind == PointerDeviceKind.touch ||
        event.kind == PointerDeviceKind.stylus;

    if (isMiddle) {
      tracked.panning = true;
      _game.panByScreenDelta(event.delta);
      return;
    }

    if (isPrimaryOrTouch) {
      if (!tracked.panning) {
        final totalDelta = event.localPosition - tracked.startPos;
        if (totalDelta.distance >= 3.0) {
          tracked.panning = true;
          _game.panByScreenDelta(totalDelta);
        }
      } else {
        _game.panByScreenDelta(event.delta);
      }
    }
  }

  void _onPointerUpOrCancel(int pointer) {
    _pointers.remove(pointer);
  }

  void _onPointerSignal(PointerSignalEvent signal) {
    if (signal is! PointerScrollEvent || signal.scrollDelta.dy == 0) return;
    GestureBinding.instance.pointerSignalResolver.register(signal, (resolved) {
      if (!mounted || resolved is! PointerScrollEvent) return;
      final factor = math.exp(-resolved.scrollDelta.dy * 0.0015);
      _game.zoomBy(
        factor,
        focalPoint: resolved.localPosition,
        viewportSize: _viewportSize,
      );
    });
  }

  void _onPanZoomStart(PointerPanZoomStartEvent event) {
    _lastPanZoomScale = 1.0;
  }

  void _onPanZoomUpdate(PointerPanZoomUpdateEvent event) {
    if (event.panDelta != Offset.zero) {
      _game.panByScreenDelta(event.panDelta);
    }
    if ((event.scale - _lastPanZoomScale).abs() > 1e-4 &&
        _lastPanZoomScale > 0) {
      _game.zoomBy(
        event.scale / _lastPanZoomScale,
        focalPoint: event.localPosition,
        viewportSize: _viewportSize,
      );
      _lastPanZoomScale = event.scale;
    }
  }

  @override
  void dispose() {
    HardwareKeyboard.instance.removeHandler(_onHardwareKey);
    _focusNode.dispose();
    unawaited(_refresh?.cancel());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final w = constraints.maxWidth.isFinite && constraints.maxWidth > 0
          ? constraints.maxWidth
          : 800.0;
      final h = constraints.maxHeight.isFinite && constraints.maxHeight > 0
          ? constraints.maxHeight
          : 500.0;
      _viewportSize = Size(w, h);

      return Semantics(
        identifier: 'scene:${widget.sceneKey}',
        label: 'Scene ${widget.sceneKey}',
        container: true,
        explicitChildNodes: true,
        child: Focus(
          focusNode: _focusNode,
          autofocus: false,
          child: Stack(
            fit: StackFit.expand,
            children: [
              Listener(
                behavior: HitTestBehavior.opaque,
                onPointerDown: _onPointerDown,
                onPointerMove: _onPointerMove,
                onPointerUp: (e) => _onPointerUpOrCancel(e.pointer),
                onPointerCancel: (e) => _onPointerUpOrCancel(e.pointer),
                onPointerSignal: _onPointerSignal,
                onPointerPanZoomStart: _onPanZoomStart,
                onPointerPanZoomUpdate: _onPanZoomUpdate,
                child: GameWidget<SceneGame>(game: _game, autofocus: false),
              ),
              if (widget.showCameraHud)
                Positioned(
                  right: 12,
                  bottom: 12,
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.end,
                    children: [
                      _FlameViewportMinimap(
                        game: _game,
                        viewportSize: _viewportSize,
                      ),
                      const SizedBox(height: 6),
                      _FlameCameraHud(game: _game, viewportSize: _viewportSize),
                    ],
                  ),
                ),
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
          ),
        ),
      );
    },
  );
}

final class _MinimapProjection {
  const _MinimapProjection({
    required this.worldBounds,
    required this.scale,
    required this.offsetX,
    required this.offsetY,
  });

  final Rect worldBounds;
  final double scale;
  final double offsetX;
  final double offsetY;

  static _MinimapProjection compute({
    required Size minimapSize,
    required Rect? sceneBounds,
    required Rect cameraWorldRect,
  }) {
    final base = sceneBounds ?? const Rect.fromLTRB(-220, -140, 220, 140);
    final merged = base.expandToInclude(cameraWorldRect).inflate(48.0);
    final worldW = math.max(merged.width, 160.0);
    final worldH = math.max(merged.height, 100.0);
    final pad = 6.0;
    final availW = math.max(minimapSize.width - pad * 2, 16.0);
    final availH = math.max(minimapSize.height - pad * 2, 16.0);
    final scale = math.min(availW / worldW, availH / worldH);
    final drawnW = worldW * scale;
    final drawnH = worldH * scale;
    final offsetX = (minimapSize.width - drawnW) / 2 - merged.left * scale;
    final offsetY = (minimapSize.height - drawnH) / 2 - merged.top * scale;
    return _MinimapProjection(
      worldBounds: merged,
      scale: scale,
      offsetX: offsetX,
      offsetY: offsetY,
    );
  }

  Offset worldToLocal(double wx, double wy) =>
      Offset(wx * scale + offsetX, wy * scale + offsetY);

  Rect worldRectToLocal(Rect r) => Rect.fromLTRB(
    r.left * scale + offsetX,
    r.top * scale + offsetY,
    r.right * scale + offsetX,
    r.bottom * scale + offsetY,
  );

  Vector2 localToWorld(Offset local) => Vector2(
    (local.dx - offsetX) / math.max(scale, 1e-6),
    (local.dy - offsetY) / math.max(scale, 1e-6),
  );
}

class _FlameViewportMinimap extends StatefulWidget {
  const _FlameViewportMinimap({required this.game, required this.viewportSize});

  static const double width = 132.0;
  static const double height = 72.0;

  final SceneGame game;
  final Size viewportSize;

  @override
  State<_FlameViewportMinimap> createState() => _FlameViewportMinimapState();
}

class _FlameViewportMinimapState extends State<_FlameViewportMinimap> {
  _MinimapProjection? _dragProjection;

  Rect _cameraWorldRect(SceneCameraSnapshot snap) {
    final z = snap.zoom.clamp(SceneGame.minZoom, SceneGame.maxZoom);
    final vw = widget.viewportSize.width > 0
        ? widget.viewportSize.width
        : 800.0;
    final vh = widget.viewportSize.height > 0
        ? widget.viewportSize.height
        : 500.0;
    return Rect.fromCenter(
      center: Offset(snap.x, snap.y),
      width: vw / z,
      height: vh / z,
    );
  }

  _MinimapProjection _currentProjection(SceneCameraSnapshot snap) =>
      _MinimapProjection.compute(
        minimapSize: const Size(
          _FlameViewportMinimap.width,
          _FlameViewportMinimap.height,
        ),
        sceneBounds: widget.game.sceneBounds(),
        cameraWorldRect: _cameraWorldRect(snap),
      );

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);

    return ValueListenableBuilder<SceneCameraSnapshot>(
      valueListenable: widget.game.cameraState,
      builder: (context, snap, _) {
        final projection = _dragProjection ?? _currentProjection(snap);
        final dots = widget.game.minimapNodes();
        final camRect = _cameraWorldRect(snap);

        return KeyedSubtree(
          key: const ValueKey('flame-viewport-minimap'),
          child: Semantics(
            identifier: 'key:flame-viewport-minimap',
            label: 'Viewport minimap',
            container: true,
            child: GestureDetector(
              behavior: HitTestBehavior.opaque,
              onTapDown: (details) {
                final worldTarget = projection.localToWorld(
                  details.localPosition,
                );
                widget.game.flyToWorld(worldTarget);
              },
              onPanStart: (details) {
                _dragProjection = projection;
                final worldTarget = projection.localToWorld(
                  details.localPosition,
                );
                widget.game.flyToWorld(worldTarget, animate: false);
              },
              onPanUpdate: (details) {
                final activeProj = _dragProjection ?? projection;
                final worldTarget = activeProj.localToWorld(
                  details.localPosition,
                );
                widget.game.flyToWorld(worldTarget, animate: false);
              },
              onPanEnd: (_) => setState(() => _dragProjection = null),
              onPanCancel: () => setState(() => _dragProjection = null),
              child: Container(
                width: _FlameViewportMinimap.width,
                height: _FlameViewportMinimap.height,
                decoration: BoxDecoration(
                  color: c.elevated.withValues(alpha: 0.90),
                  borderRadius: BorderRadius.circular(tokens.card),
                  border: Border.all(color: c.hairline),
                ),
                clipBehavior: Clip.antiAlias,
                child: CustomPaint(
                  size: const Size(
                    _FlameViewportMinimap.width,
                    _FlameViewportMinimap.height,
                  ),
                  painter: _MinimapPainter(
                    projection: projection,
                    dots: dots,
                    cameraWorldRect: camRect,
                    accentColor: c.accent,
                    gridColor: c.hairline.withValues(alpha: 0.45),
                  ),
                ),
              ),
            ),
          ),
        );
      },
    );
  }
}

class _MinimapPainter extends CustomPainter {
  const _MinimapPainter({
    required this.projection,
    required this.dots,
    required this.cameraWorldRect,
    required this.accentColor,
    required this.gridColor,
  });

  final _MinimapProjection projection;
  final List<MinimapNodeDot> dots;
  final Rect cameraWorldRect;
  final Color accentColor;
  final Color gridColor;

  @override
  void paint(Canvas canvas, Size size) {
    final gridPaint = Paint()
      ..color = gridColor
      ..strokeWidth = 0.7;
    canvas.drawLine(
      Offset(size.width / 2, 0),
      Offset(size.width / 2, size.height),
      gridPaint,
    );
    canvas.drawLine(
      Offset(0, size.height / 2),
      Offset(size.width, size.height / 2),
      gridPaint,
    );

    for (final dot in dots) {
      final pt = projection.worldToLocal(dot.x, dot.y);
      if (dot.active) {
        canvas.drawCircle(
          pt,
          5.0,
          Paint()..color = dot.color.withValues(alpha: 0.30),
        );
      }
      canvas.drawCircle(pt, dot.active ? 3.2 : 2.5, Paint()..color = dot.color);
    }

    final rawCam = projection.worldRectToLocal(cameraWorldRect);
    final clampedCam = Rect.fromLTRB(
      rawCam.left.clamp(1.0, size.width - 5.0),
      rawCam.top.clamp(1.0, size.height - 5.0),
      rawCam.right.clamp(5.0, size.width - 1.0),
      rawCam.bottom.clamp(5.0, size.height - 1.0),
    );
    canvas.drawRRect(
      RRect.fromRectAndRadius(clampedCam, const Radius.circular(3)),
      Paint()..color = accentColor.withValues(alpha: 0.14),
    );
    canvas.drawRRect(
      RRect.fromRectAndRadius(clampedCam, const Radius.circular(3)),
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.2
        ..color = accentColor.withValues(alpha: 0.90),
    );
  }

  @override
  bool shouldRepaint(covariant _MinimapPainter oldDelegate) =>
      oldDelegate.cameraWorldRect != cameraWorldRect ||
      oldDelegate.dots.length != dots.length ||
      oldDelegate.projection.worldBounds != projection.worldBounds;
}

class _FlameCameraHud extends StatelessWidget {
  const _FlameCameraHud({required this.game, required this.viewportSize});

  final SceneGame game;
  final Size viewportSize;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);

    return ValueListenableBuilder<SceneCameraSnapshot>(
      valueListenable: game.cameraState,
      builder: (context, snap, _) {
        final canZoomOut = snap.zoom > SceneGame.minZoom + 1e-4;
        final canZoomIn = snap.zoom < SceneGame.maxZoom - 1e-4;

        return Semantics(
          identifier: 'key:flame-camera-hud',
          container: true,
          explicitChildNodes: true,
          child: Container(
            key: const ValueKey('flame-camera-hud'),
            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 4),
            decoration: BoxDecoration(
              color: c.elevated.withValues(alpha: 0.92),
              borderRadius: BorderRadius.circular(tokens.pill),
              border: Border.all(color: c.hairline),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                _HudButton(
                  keyId: 'flame-follow-live',
                  label: 'Follow Live',
                  semanticLabel: 'Follow Live',
                  icon: LucideIcons.radio,
                  showText: true,
                  active: snap.followLive,
                  onTap: game.toggleFollowLive,
                ),
                Container(
                  width: 1,
                  height: 14,
                  margin: const EdgeInsets.symmetric(horizontal: 6),
                  color: c.hairline,
                ),
                _HudButton(
                  keyId: 'flame-zoom-out',
                  label: '-',
                  semanticLabel: 'Zoom out',
                  icon: LucideIcons.minus,
                  enabled: canZoomOut,
                  onTap: () => game.zoomOut(viewportSize: viewportSize),
                ),
                const SizedBox(width: 4),
                Semantics(
                  identifier: 'key:flame-zoom-readout',
                  container: true,
                  label: '${snap.zoomPercent}%',
                  child: Container(
                    key: const ValueKey('flame-zoom-readout'),
                    constraints: const BoxConstraints(minWidth: 44),
                    padding: const EdgeInsets.symmetric(
                      horizontal: 6,
                      vertical: 3,
                    ),
                    alignment: Alignment.center,
                    child: Text(
                      '${snap.zoomPercent}%',
                      style: UiType.tabularStyle(
                        11.5,
                        weight: FontWeight.w600,
                        color: c.strong,
                      ),
                    ),
                  ),
                ),
                const SizedBox(width: 4),
                _HudButton(
                  keyId: 'flame-zoom-in',
                  label: '+',
                  semanticLabel: 'Zoom in',
                  icon: LucideIcons.plus,
                  enabled: canZoomIn,
                  onTap: () => game.zoomIn(viewportSize: viewportSize),
                ),
                Container(
                  width: 1,
                  height: 14,
                  margin: const EdgeInsets.symmetric(horizontal: 6),
                  color: c.hairline,
                ),
                _HudButton(
                  keyId: 'flame-fit',
                  label: 'Fit',
                  semanticLabel: 'Fit to graph',
                  icon: LucideIcons.maximize2,
                  showText: true,
                  onTap: () => game.fitToGraph(viewportSize: viewportSize),
                ),
                const SizedBox(width: 4),
                _HudButton(
                  keyId: 'flame-reset',
                  label: 'Reset',
                  semanticLabel: 'Reset camera',
                  icon: LucideIcons.rotateCcw,
                  showText: true,
                  onTap: game.resetCamera,
                ),
              ],
            ),
          ),
        );
      },
    );
  }
}

class _HudButton extends StatelessWidget {
  const _HudButton({
    required this.keyId,
    required this.label,
    required this.semanticLabel,
    required this.icon,
    required this.onTap,
    this.enabled = true,
    this.showText = false,
    this.active = false,
  });

  final String keyId;
  final String label;
  final String semanticLabel;
  final IconData icon;
  final VoidCallback onTap;
  final bool enabled;
  final bool showText;
  final bool active;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final fg = active
        ? c.accent
        : (enabled ? c.strong : c.muted.withValues(alpha: 0.45));

    return KeyedSubtree(
      key: ValueKey(keyId),
      child: Semantics(
        identifier: 'key:$keyId',
        button: true,
        toggled: active ? true : null,
        enabled: enabled,
        label: semanticLabel,
        container: true,
        child: Tooltip(
          message: semanticLabel,
          child: Material(
            type: MaterialType.transparency,
            child: InkWell(
              onTap: enabled ? onTap : null,
              borderRadius: BorderRadius.circular(tokens.pill),
              child: Container(
                height: 24,
                constraints: BoxConstraints(minWidth: showText ? 42 : 24),
                padding: EdgeInsets.symmetric(horizontal: showText ? 8 : 5),
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  color: active
                      ? c.accent.withValues(alpha: 0.16)
                      : c.recessed.withValues(alpha: 0.7),
                  borderRadius: BorderRadius.circular(tokens.pill),
                  border: active
                      ? Border.all(color: c.accent.withValues(alpha: 0.65))
                      : null,
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(icon, size: 12, color: fg),
                    if (showText) ...[
                      const SizedBox(width: 4),
                      Text(
                        label,
                        style: UiType.textStyle(
                          11,
                          weight: FontWeight.w600,
                          color: fg,
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
