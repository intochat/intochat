import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

SceneState _makeScene(
  List<SceneNode> nodes, {
  SceneCamera camera = const SceneCamera(x: 0, y: 0, zoom: 1.0),
}) => SceneState(
  name: 'test-scene',
  revision: 1,
  definition: SceneDefinition(nodes: nodes, camera: camera),
);

SceneNode _circle(
  String id, {
  double x = 0,
  double y = 0,
  double radius = 30,
  List<String> inputs = const [],
}) => SceneNode(
  id: id,
  type: 'circle',
  transform: SceneTransform(x: x, y: y),
  style: '#4FC3F7',
  parameters: {'radius': '$radius'},
  inputs: inputs,
);

Widget _wrap(Widget child) => MaterialApp(
  theme: UiTheme.light(null),
  home: Scaffold(
    body: Center(
      child: SizedBox(width: 800, height: 500, child: child),
    ),
  ),
);

void main() {
  group('SceneGame camera math & clamping', () {
    test('zoom clamps strictly to [0.35, 3.0] and anchors focal point', () {
      final game = SceneGame()
        ..apply(_makeScene([_circle('a', x: 100, y: 50)]));

      game.setZoom(0.05, viewportSize: const Size(800, 500));
      expect(game.zoom, closeTo(SceneGame.minZoom, 1e-6));
      expect(game.cameraState.value.zoomPercent, 35);

      game.setZoom(9.5, viewportSize: const Size(800, 500));
      expect(game.zoom, closeTo(SceneGame.maxZoom, 1e-6));
      expect(game.cameraState.value.zoomPercent, 300);

      game.resetCamera();
      expect(game.zoom, closeTo(1.0, 1e-6));
      expect(game.cameraPosition.x, closeTo(0, 1e-6));
      expect(game.cameraPosition.y, closeTo(0, 1e-6));

      // Zoom 1.0 -> 2.0 anchored at focal point (500, 250) on an 800x500 viewport (+100px right of center).
      // World point (100, 0) must remain under screen point (500, 250), so camera center shifts to (50, 0).
      game.setZoom(
        2.0,
        focalPoint: const Offset(500, 250),
        viewportSize: const Size(800, 500),
      );
      expect(game.zoom, closeTo(2.0, 1e-6));
      expect(game.cameraPosition.x, closeTo(50.0, 1e-4));
      expect(game.cameraPosition.y, closeTo(0.0, 1e-4));
    });

    test('fitToGraph centers bounding box of active nodes and scales with padding', () {
      final game = SceneGame()
        ..apply(
          _makeScene([
            _circle('left', x: -300, y: -100, radius: 40),
            _circle('right', x: 500, y: 200, radius: 40),
          ]),
        );

      game.fitToGraph(padding: 60, viewportSize: const Size(800, 500));

      // Bounds: x in [-340, 540] -> center.x = 100, width = 880 (+120 pad = 1000)
      //         y in [-140, 240] -> center.y = 50,  height = 380 (+120 pad = 500)
      // Fit zoom = min(800 / 1000, 500 / 500) = 0.8
      expect(game.cameraPosition.x, closeTo(100, 1e-3));
      expect(game.cameraPosition.y, closeTo(50, 1e-3));
      expect(game.zoom, closeTo(0.8, 1e-3));
    });

    test('refreshing scene state preserves user-panned and zoomed camera', () {
      final game = SceneGame()
        ..apply(_makeScene([_circle('a', x: 0, y: 0)]));
      game.panByScreenDelta(const Offset(-120, 60));
      game.setZoom(1.5, viewportSize: const Size(800, 500));

      final savedPos = game.cameraPosition.clone();
      final savedZoom = game.zoom;

      game.apply(
        SceneState(
          name: 'test-scene',
          revision: 2,
          definition: SceneDefinition(
            nodes: [_circle('a', x: 0, y: 0), _circle('b', x: 120, y: 40)],
            camera: const SceneCamera(x: 0, y: 0, zoom: 1.0),
          ),
        ),
      );

      expect(game.zoom, closeTo(savedZoom, 1e-6));
      expect(game.cameraPosition.x, closeTo(savedPos.x, 1e-6));
      expect(game.cameraPosition.y, closeTo(savedPos.y, 1e-6));
    });
  });

  group('SceneView gestures, HUD semantics & WASD', () {
    testWidgets(
      'background drag pans camera without breaking node tap or draggable node drag',
      (tester) async {
        final game = SceneGame();
        final tapped = <String>[];
        final state = _makeScene([
          _circle('tap-node', x: -150, y: 0, radius: 32, inputs: const ['tap']),
          _circle(
            'drag-node',
            x: 150,
            y: 0,
            radius: 32,
            inputs: const ['drag'],
          ),
        ]);

        await tester.pumpWidget(
          _wrap(
            SceneView(
              sceneKey: 'test',
              state: state,
              game: game,
              onInput: (id, kind, x, y, _) => tapped.add('$id:$kind'),
            ),
          ),
        );
        await tester.pump();

        final viewRect = tester.getRect(find.byType(SceneView));
        final center = viewRect.center;

        // 1. Background mouse drag pans camera smoothly
        final bgDrag = await tester.startGesture(
          center,
          kind: PointerDeviceKind.mouse,
        );
        await bgDrag.moveBy(const Offset(80, -40));
        await bgDrag.up();
        await tester.pump();

        expect(game.cameraPosition.x, closeTo(-80, 1e-2));
        expect(game.cameraPosition.y, closeTo(40, 1e-2));

        // Reset before testing draggable node hit-testing
        game.resetCamera();
        await tester.pump();

        // 2. Dragging on drag-node (x: +150 from center) moves node, NOT camera
        final dragNodeScreen = center + const Offset(150, 0);
        final nodeDrag = await tester.startGesture(
          dragNodeScreen,
          kind: PointerDeviceKind.mouse,
        );
        await nodeDrag.moveBy(const Offset(30, 20));
        await nodeDrag.up();
        await tester.pump();

        expect(game.cameraPosition.x, closeTo(0, 1e-3));
        expect(game.cameraPosition.y, closeTo(0, 1e-3));
        expect(game.componentFor('drag-node')!.position.x, closeTo(180, 1));

        // 3. Middle-mouse drag pans camera even when starting over drag-node
        final middleDrag = await tester.startGesture(
          center + const Offset(180, 20),
          kind: PointerDeviceKind.mouse,
          buttons: kMiddleMouseButton,
        );
        await middleDrag.moveBy(const Offset(-50, 25));
        await middleDrag.up();
        await tester.pump();

        expect(game.cameraPosition.x, closeTo(50, 1e-2));
        expect(game.cameraPosition.y, closeTo(-25, 1e-2));

        // 4. Node drag reported earlier and node tap still reports cleanly
        game.simulateTap('tap-node');
        expect(tapped, ['drag-node:drag', 'tap-node:tap']);
        await tester.pump(const Duration(milliseconds: 100));
      },
    );

    testWidgets(
      'scroll wheel, pinch zoom, WASD pan, and HUD + / - / Fit / Reset semantics work end-to-end',
      (tester) async {
        final semantics = tester.ensureSemantics();
        final game = SceneGame();
        final state = _makeScene([
          _circle('n1', x: -240, y: -120, radius: 36),
          _circle('n2', x: 240, y: 120, radius: 36),
        ]);

        await tester.pumpWidget(
          _wrap(SceneView(sceneKey: 'hud-test', state: state, game: game)),
        );
        await tester.pump();

        // Verify initial HUD readout & semantics identifiers
        expect(find.text('100%'), findsOneWidget);
        expect(
          find.bySemanticsLabel(RegExp('Zoom in')),
          findsOneWidget,
        );
        expect(
          find.bySemanticsLabel(RegExp('Zoom out')),
          findsOneWidget,
        );
        expect(
          find.bySemanticsLabel(RegExp('Fit to graph')),
          findsOneWidget,
        );
        expect(
          find.bySemanticsLabel(RegExp('Reset camera')),
          findsOneWidget,
        );

        // Click HUD '+' (flame-zoom-in)
        await tester.tap(find.byKey(const ValueKey('flame-zoom-in')));
        await tester.pump();
        expect(game.zoom, closeTo(1.25, 1e-3));
        expect(find.text('125%'), findsOneWidget);

        // Click HUD '-' (flame-zoom-out) twice
        await tester.tap(find.byKey(const ValueKey('flame-zoom-out')));
        await tester.pump();
        await tester.tap(find.byKey(const ValueKey('flame-zoom-out')));
        await tester.pump();
        expect(game.zoom, closeTo(0.8, 1e-3));
        expect(find.text('80%'), findsOneWidget);

        // Mouse scroll wheel zoom clamping
        final center = tester.getCenter(find.byType(SceneView));
        final mouse = TestPointer(1, PointerDeviceKind.mouse);
        await tester.sendEventToBinding(mouse.hover(center));
        await tester.sendEventToBinding(mouse.scroll(const Offset(0, -3000)));
        await tester.pump();
        expect(game.zoom, closeTo(SceneGame.maxZoom, 1e-4));
        expect(find.text('300%'), findsOneWidget);

        await tester.sendEventToBinding(mouse.scroll(const Offset(0, 5000)));
        await tester.pump();
        expect(game.zoom, closeTo(SceneGame.minZoom, 1e-4));
        expect(find.text('35%'), findsOneWidget);

        // 2-finger pinch zoom in
        final f1 = await tester.startGesture(
          center - const Offset(40, 0),
          kind: PointerDeviceKind.touch,
        );
        final f2 = await tester.startGesture(
          center + const Offset(40, 0),
          kind: PointerDeviceKind.touch,
        );
        await f1.moveBy(const Offset(-40, 0));
        await f2.moveBy(const Offset(40, 0));
        await f1.up();
        await f2.up();
        await tester.pump();
        expect(game.zoom, closeTo(0.7, 0.05));

        // Fit button centers & fits graph
        await tester.tap(find.byKey(const ValueKey('flame-fit')));
        await tester.pump();
        expect(game.cameraPosition.x, closeTo(0, 1e-2));
        expect(game.cameraPosition.y, closeTo(0, 1e-2));

        // WASD keyboard panning
        await tester.sendKeyDownEvent(LogicalKeyboardKey.keyW);
        await tester.sendKeyUpEvent(LogicalKeyboardKey.keyW);
        await tester.sendKeyDownEvent(LogicalKeyboardKey.keyD);
        await tester.sendKeyUpEvent(LogicalKeyboardKey.keyD);
        await tester.pump();
        expect(game.cameraPosition.y, lessThan(0));
        expect(game.cameraPosition.x, greaterThan(0));

        // Reset button restores 1.0x (100%) at (0, 0)
        await tester.tap(find.byKey(const ValueKey('flame-reset')));
        await tester.pump();
        expect(game.zoom, closeTo(1.0, 1e-6));
        expect(game.cameraPosition.x, closeTo(0, 1e-6));
        expect(game.cameraPosition.y, closeTo(0, 1e-6));
        expect(find.text('100%'), findsOneWidget);

        await tester.pump(const Duration(milliseconds: 100));
        semantics.dispose();
      },
    );
  });

  group('BrainScene & StateGraphView camera + replay shortcut inheritance', () {
    testWidgets(
      'BrainSceneView inherits camera HUD and dispatches ArrowLeft/ArrowRight/Space/Home/End replay keys',
      (tester) async {
        final activations = <String>[];
        final ctx = NodeContext(
          view: 'local/apps/creator/view',
          send: (event) async {
            activations.add('${event['key']}:${event['value']}');
            return null;
          },
        );
        final node = BrainSceneNode.fromJson({
          'kind': 'brainScene',
          'pauseRotation': true,
          'nodes': [
            {
              'id': 's0',
              'label': 'Ready',
              'uri': 'state://creator/s0',
              'kind': 'initial',
              'x': 0,
              'y': 0,
              'z': 0,
            },
            {
              'id': 's1',
              'label': 'Selected',
              'uri': 'state://creator/s1',
              'kind': 'state',
              'x': 230,
              'y': 0,
              'z': 0,
            },
          ],
          'synapses': [
            {
              'fromId': 's0',
              'toId': 's1',
              'signalLabel': 'SelectNode',
              'active': true,
            },
            {
              'fromId': 's0',
              'toId': 's0',
              'signalLabel': 'ValidateMake [rejected: empty_draft]',
              'active': false,
            },
          ],
        });

        await tester.pumpWidget(_wrap(BrainSceneView(node: node, ctx: ctx)));
        await tester.pump();

        // BrainScene / StateGraphView starts at 50% zoom and inherits HUD controls
        expect(find.text('50%'), findsOneWidget);
        await tester.tap(find.byKey(const ValueKey('flame-reset')));
        await tester.pump();
        expect(find.text('100%'), findsOneWidget);

        // Time-travel keyboard bindings dispatch replay commands
        await tester.sendKeyDownEvent(LogicalKeyboardKey.arrowRight);
        await tester.sendKeyUpEvent(LogicalKeyboardKey.arrowRight);
        await tester.sendKeyDownEvent(LogicalKeyboardKey.arrowLeft);
        await tester.sendKeyUpEvent(LogicalKeyboardKey.arrowLeft);
        await tester.sendKeyDownEvent(LogicalKeyboardKey.space);
        await tester.sendKeyUpEvent(LogicalKeyboardKey.space);
        await tester.sendKeyDownEvent(LogicalKeyboardKey.home);
        await tester.sendKeyUpEvent(LogicalKeyboardKey.home);
        await tester.sendKeyDownEvent(LogicalKeyboardKey.end);
        await tester.sendKeyUpEvent(LogicalKeyboardKey.end);
        await tester.pump();

        expect(activations, [
          'replay-next:null',
          'replay-prev:null',
          'replay-toggle:null',
          'replay-first:null',
          'replay-last:null',
        ]);

        // Also verify IScreen act-route (`actsViaAct: true`) sends `creator.replay`
        final actEvents = <Map<String, dynamic>>[];
        final actCtx = NodeContext(
          view: 'local/apps/creator/view',
          actsViaAct: true,
          send: (event) async {
            actEvents.add(event);
            return null;
          },
        );
        await tester.pumpWidget(_wrap(BrainSceneView(node: node, ctx: actCtx)));
        await tester.pump();
        await tester.sendKeyDownEvent(LogicalKeyboardKey.arrowLeft);
        await tester.sendKeyUpEvent(LogicalKeyboardKey.arrowLeft);
        await tester.pump();

        expect(actEvents, hasLength(1));
        expect(actEvents.single['signal'], 'creator.replay');
        expect(actEvents.single['args'], {'cursor': 'prev'});
      },
    );
  });
}
