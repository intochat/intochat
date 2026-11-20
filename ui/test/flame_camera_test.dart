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
    body: Center(child: SizedBox(width: 800, height: 500, child: child)),
  ),
);

void main() {
  group('SceneGame camera math, clamping, flyToNode & Follow Live', () {
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

      game.setZoom(
        2.0,
        focalPoint: const Offset(500, 250),
        viewportSize: const Size(800, 500),
      );
      expect(game.zoom, closeTo(2.0, 1e-6));
      expect(game.cameraPosition.x, closeTo(50.0, 1e-4));
      expect(game.cameraPosition.y, closeTo(0.0, 1e-4));
    });

    test(
      'fitToGraph centers bounding box of active nodes and scales with padding',
      () {
        final game = SceneGame()
          ..apply(
            _makeScene([
              _circle('left', x: -300, y: -100, radius: 40),
              _circle('right', x: 500, y: 200, radius: 40),
            ]),
          );

        game.fitToGraph(padding: 60, viewportSize: const Size(800, 500));

        expect(game.cameraPosition.x, closeTo(100, 1e-3));
        expect(game.cameraPosition.y, closeTo(50, 1e-3));
        expect(game.zoom, closeTo(0.8, 1e-3));
      },
    );

    test('refreshing scene state preserves user-panned and zoomed camera', () {
      final game = SceneGame()..apply(_makeScene([_circle('a', x: 0, y: 0)]));
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

    test('flyToNode and Follow Live smoothly interpolate camera to active progressing node', () {
      final game = SceneGame()
        ..apply(
          _makeScene([
            const SceneNode(
              id: 'plan',
              type: 'sphere',
              transform: SceneTransform(x: -200, y: 0),
              parameters: {
                'title': 'Ratify Plan',
                'nodeStatus': 'done',
                'progress': '100%',
              },
            ),
            const SceneNode(
              id: 'slice-2',
              type: 'sphere',
              transform: SceneTransform(x: 320, y: -80),
              parameters: {
                'title': 'Ino Slice 2',
                'nodeStatus': 'working · 65%',
              },
            ),
          ]),
        );

      final sliceComp = game.componentFor('slice-2') as SphereComponent;
      expect(sliceComp.progress, closeTo(0.65, 1e-3));

      // Enable Follow Live: auto-selects progressing slice-2 at (320, -80) and tweens smoothly!
      game.toggleFollowLive();
      expect(game.followLive, isTrue);
      expect(game.followTargetId, 'slice-2');
      expect(game.isCameraFlying, isTrue);

      game.update(0.10);
      expect(game.cameraPosition.x, inExclusiveRange(10, 319));
      expect(game.cameraPosition.y, inExclusiveRange(-79, -1));

      game.update(0.25);
      expect(game.isCameraFlying, isFalse);
      expect(game.cameraPosition.x, closeTo(320, 1e-2));
      expect(game.cameraPosition.y, closeTo(-80, 1e-2));
    });
  });

  group(
    '3D Icon Nodes, Circular Progress Rings & 3 Accordant DDD Edge Styles',
    () {
      test('brainSceneState maps 0-100% progress rings and accepted, candidate, and refused Rule edges', () {
        final brainNode = BrainSceneNode.fromJson({
          'kind': 'brainScene',
          'selectedId': 'slice-1',
          'nodes': [
            {
              'id': 'plan',
              'label': 'Ratify Plan',
              'uri': 'state://engineer/plan',
              'kind': 'initial',
              'status': '100%',
              'x': 0,
              'y': 0,
              'z': 0,
            },
            {
              'id': 'slice-1',
              'label': 'Slice 1 · Contracts',
              'subtitle': 'InoNative · 60%',
              'uri': 'state://engineer/slice-1',
              'kind': 'slice',
              'status': 'working',
              'x': 240,
              'y': -80,
              'z': 0,
            },
            {
              'id': 'slice-2',
              'label': 'Candidate w2',
              'subtitle': 'Clean Refactor · 25%',
              'uri': 'state://engineer/w2',
              'kind': 'worktree',
              'status': 'pending',
              'x': 240,
              'y': 90,
              'z': 0,
            },
          ],
          'synapses': [
            {
              'fromId': 'plan',
              'toId': 'slice-1',
              'signalLabel': 'RatifyEngineerPlan',
              'active': true,
            },
            {
              'fromId': 'plan',
              'toId': 'slice-2',
              'signalLabel': 'candidate:w2',
              'active': false,
            },
            {
              'fromId': 'slice-1',
              'toId': 'slice-1',
              'signalLabel': 'ApprovePr [rejected: requires_verified_sha]',
              'active': false,
            },
          ],
        });

        final game = SceneGame()..apply(brainSceneState(brainNode));

        final planSphere = game.componentFor('plan') as SphereComponent;
        final slice1Sphere = game.componentFor('slice-1') as SphereComponent;
        final slice2Sphere = game.componentFor('slice-2') as SphereComponent;

        expect(planSphere.progress, closeTo(1.0, 1e-3));
        expect(slice1Sphere.progress, closeTo(0.60, 1e-3));
        expect(slice2Sphere.progress, closeTo(0.25, 1e-3));
        expect(slice1Sphere.params['icon'], 'cpu');
        expect(slice2Sphere.params['icon'], 'git-branch');

        final acceptedEdge = game.componentFor(
          'edge:plan→slice-1:RatifyEngineerPlan',
        ) as PathNode;
        final candidateEdge =
            game.componentFor('edge:plan→slice-2:candidate:w2') as PathNode;
        final refusedEdge = game.componentFor(
          'edge:slice-1→slice-1:ApprovePr [rejected: requires_verified_sha]',
        ) as PathNode;

        expect(acceptedEdge.accordantStyle, AccordantEdgeStyle.accepted);
        expect(acceptedEdge.hasDirectionalArrow, isTrue);
        expect(acceptedEdge.params['activePulse'], 'true');

        expect(candidateEdge.accordantStyle, AccordantEdgeStyle.candidate);
        expect(candidateEdge.hasDirectionalArrow, isTrue);

        expect(refusedEdge.accordantStyle, AccordantEdgeStyle.refused);
        expect(refusedEdge.hasDirectionalArrow, isTrue);
        expect(refusedEdge.badgeLabel, '✕ requires_verified_sha');
      });
    },
  );

  group('SceneView gestures, HUD, Follow Live, Minimap & WASD', () {
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

        final bgDrag = await tester.startGesture(
          center,
          kind: PointerDeviceKind.mouse,
        );
        await bgDrag.moveBy(const Offset(80, -40));
        await bgDrag.up();
        await tester.pump();

        expect(game.cameraPosition.x, closeTo(-80, 1e-2));
        expect(game.cameraPosition.y, closeTo(40, 1e-2));

        game.resetCamera();
        await tester.pump();

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

        game.simulateTap('tap-node');
        expect(tapped, ['drag-node:drag', 'tap-node:tap']);
        await tester.pump(const Duration(milliseconds: 100));
      },
    );

    testWidgets(
      'scroll wheel, pinch zoom, WASD, Follow Live, 132x72 Minimap click/drag, and HUD semantics work end-to-end',
      (tester) async {
        final semantics = tester.ensureSemantics();
        final game = SceneGame();
        final state = _makeScene([
          const SceneNode(
            id: 'n1',
            type: 'sphere',
            transform: SceneTransform(x: -240, y: -120),
            parameters: {'title': 'Start', 'nodeStatus': 'done'},
          ),
          const SceneNode(
            id: 'n2',
            type: 'sphere',
            transform: SceneTransform(x: 260, y: 140),
            parameters: {'title': 'Live Slice', 'nodeStatus': 'working · 50%'},
          ),
        ]);

        await tester.pumpWidget(
          _wrap(SceneView(sceneKey: 'hud-test', state: state, game: game)),
        );
        await tester.pump();

        expect(find.text('100%'), findsOneWidget);
        expect(find.bySemanticsLabel(RegExp('Follow Live')), findsOneWidget);
        expect(
          find.bySemanticsLabel(RegExp('Viewport minimap')),
          findsOneWidget,
        );
        expect(find.bySemanticsLabel(RegExp('Zoom in')), findsOneWidget);
        expect(find.bySemanticsLabel(RegExp('Zoom out')), findsOneWidget);
        expect(find.bySemanticsLabel(RegExp('Fit to graph')), findsOneWidget);
        expect(find.bySemanticsLabel(RegExp('Reset camera')), findsOneWidget);

        // Verify Corner Viewport Minimap is 132x72px and interactive
        final minimapFinder = find.byKey(
          const ValueKey('flame-viewport-minimap'),
        );
        final minimapRect = tester.getRect(minimapFinder);
        expect(minimapRect.width, closeTo(132, 0.1));
        expect(minimapRect.height, closeTo(72, 0.1));
        expect(game.minimapNodes(), hasLength(2));

        // Tapping upper-left of minimap flies camera toward negative world coordinates
        await tester.tapAt(minimapRect.topLeft + const Offset(16, 12));
        await tester.pump(const Duration(milliseconds: 320));
        expect(game.cameraPosition.x, lessThan(-50));
        expect(game.cameraPosition.y, lessThan(-25));

        // Tapping Follow Live centers smoothly on active 50%-progress node 'n2' at (260, 140)
        await tester.tap(find.byKey(const ValueKey('flame-follow-live')));
        await tester.pump(const Duration(milliseconds: 320));
        expect(game.followLive, isTrue);
        expect(game.cameraPosition.x, closeTo(260, 1e-1));
        expect(game.cameraPosition.y, closeTo(140, 1e-1));

        // Reset before zoom/pan checks
        await tester.tap(find.byKey(const ValueKey('flame-reset')));
        await tester.pump();
        expect(game.followLive, isFalse);
        expect(game.zoom, closeTo(1.0, 1e-6));

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
        expect(game.cameraPosition.x, closeTo(10, 1e-2));

        // WASD keyboard panning
        final beforeY = game.cameraPosition.y;
        final beforeX = game.cameraPosition.x;
        await tester.sendKeyDownEvent(LogicalKeyboardKey.keyW);
        await tester.sendKeyUpEvent(LogicalKeyboardKey.keyW);
        await tester.sendKeyDownEvent(LogicalKeyboardKey.keyD);
        await tester.sendKeyUpEvent(LogicalKeyboardKey.keyD);
        await tester.pump();
        expect(game.cameraPosition.y, lessThan(beforeY));
        expect(game.cameraPosition.x, greaterThan(beforeX));

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
      'BrainSceneView stretches full-bleed in bounded boxes, inherits HUD + Minimap, and dispatches replay keys',
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

        await tester.pumpWidget(
          MaterialApp(
            theme: UiTheme.light(null),
            home: Scaffold(
              body: SizedBox(
                width: 760,
                height: 560,
                child: renderBrainScene(node, ctx),
              ),
            ),
          ),
        );
        await tester.pump();

        // Full-bleed stretching inside bounded 560px parent box (> 420px fallback)!
        final renderedRect = tester.getRect(find.byType(BrainSceneView));
        expect(renderedRect.height, closeTo(560, 0.1));

        expect(find.text('50%'), findsOneWidget);
        expect(
          find.byKey(const ValueKey('flame-viewport-minimap')),
          findsOneWidget,
        );
        expect(find.byKey(const ValueKey('flame-follow-live')), findsOneWidget);

        await tester.tap(find.byKey(const ValueKey('flame-reset')));
        await tester.pump();
        expect(find.text('100%'), findsOneWidget);

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
