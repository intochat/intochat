import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/stage/stage_controller.dart';
import 'package:digitalbrain_flutter_shell/stage/stage_view.dart';
import 'package:digitalbrain_flutter_shell/workspace/application_surface.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart' show SceneView;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'stage_fakes.dart';

void main() {
  testWidgets('with no home the stage view shows the classic desktop', (
    tester,
  ) async {
    final controller = StageController.fake(
      const StageState(name: 's', revision: 0, home: '', stack: []),
    );
    await tester.pumpWidget(
      MaterialApp(
        home: StageHost(
          controller: controller,
          desktop: const Text('desktop'),
          client: FakeSceneClient(),
        ),
      ),
    );
    expect(find.text('desktop'), findsOneWidget);
  });

  testWidgets(
    'the top entry renders its scene and a docked surface beside it',
    (tester) async {
      final controller = StageController.fake(
        const StageState(
          name: 's',
          revision: 2,
          home: 'owner/apps/ino/scenes/home',
          stack: [
            StageEntry(
              sceneKey: 'owner/apps/ino/scenes/home',
              dockSurfaceKey: null,
            ),
            StageEntry(
              sceneKey: 'owner/apps/ino/scenes/home',
              dockSurfaceKey: 'owner/apps/files/surface',
            ),
          ],
        ),
      );
      await tester.pumpWidget(
        MaterialApp(
          home: StageHost(
            controller: controller,
            desktop: const SizedBox(),
            client: FakeSceneClient(),
            ui: fakeUiClient(),
            workspace: 'owner',
          ),
        ),
      );
      await tester.pump();
      expect(find.byType(SceneView), findsOneWidget);
      expect(find.byType(ApplicationSurface), findsOneWidget);
      expect(
        tester.getSize(find.byType(SceneView)).width,
        StageView.dockedSceneWidth,
      );
    },
  );

  testWidgets('Escape asks the stage to go home', (tester) async {
    final client = FakeSceneClient();
    final controller = StageController.fake(
      const StageState(
        name: 's',
        revision: 1,
        home: 'owner/apps/ino/scenes/home',
        stack: [
          StageEntry(
            sceneKey: 'owner/apps/ino/scenes/home',
            dockSurfaceKey: null,
          ),
          StageEntry(
            sceneKey: 'owner/apps/brainview/scenes/brain',
            dockSurfaceKey: null,
          ),
        ],
      ),
      client: client,
    );
    await tester.pumpWidget(
      MaterialApp(
        home: StageHost(
          controller: controller,
          desktop: const SizedBox(),
          client: client,
        ),
      ),
    );
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pump();
    expect(client.calls, contains('home'));
  });

  testWidgets('Escape inside a text field does not go home', (tester) async {
    final client = FakeSceneClient();
    final controller = StageController.fake(
      const StageState(
        name: 's',
        revision: 1,
        home: 'h',
        stack: [StageEntry(sceneKey: 'h', dockSurfaceKey: null)],
      ),
      client: client,
    );
    await tester.pumpWidget(
      MaterialApp(
        home: Column(
          children: [
            const Material(child: TextField()),
            Expanded(
              child: StageHost(
                controller: controller,
                desktop: const SizedBox(),
                client: client,
              ),
            ),
          ],
        ),
      ),
    );
    await tester.tap(find.byType(TextField));
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pump();
    expect(client.calls, isNot(contains('home')));
  });

  testWidgets('a stack entry whose scene is gone is dropped on refresh', (
    tester,
  ) async {
    final client = _MissingSceneClient();
    final controller = StageController(client: client);
    await controller.refresh();
    expect(controller.stage.stack.map((e) => e.sceneKey), ['home']);
  });

  test(
    'a dropped StageChanged stream reads the stage again and follows it anew',
    () async {
      final client = _CountingClient();
      final streams = <StreamController<LiveEnvelope>>[];
      final controller = StageController(
        client: client,
        live: (_) {
          final stream = StreamController<LiveEnvelope>();
          streams.add(stream);
          return stream.stream;
        },
      )..retryDelay = Duration.zero;
      await controller.refresh();
      expect(client.stages, 1);
      expect(streams, hasLength(1));
      streams.first.addError(StateError('connection dropped'));
      await Future<void>.delayed(const Duration(milliseconds: 10));
      expect(client.stages, 2);
      expect(streams, hasLength(2));
      controller.dispose();
    },
  );

  test('a failed first stage read is retried', () async {
    final client = _CountingClient()..failFirst = true;
    final controller = StageController(client: client)
      ..retryDelay = Duration.zero;
    await controller.refresh();
    await Future<void>.delayed(const Duration(milliseconds: 10));
    expect(client.stages, 2);
    expect(controller.stage.revision, 3);
    controller.dispose();
  });

  test(
    'overlapping refreshes finishing in reverse order keep the higher revision',
    () async {
      final client = _GatedClient();
      final controller = StageController(client: client);
      final older = controller.refresh();
      final newer = controller.refresh();
      client.gates[1].complete(
        const StageState(name: 's', revision: 5, home: 'b'),
      );
      await newer;
      client.gates[0].complete(
        const StageState(name: 's', revision: 4, home: 'a'),
      );
      await older;
      expect(controller.stage.revision, 5);
      expect(controller.stage.home, 'b');
    },
  );
}

class _CountingClient extends FakeSceneClient {
  int stages = 0;
  bool failFirst = false;

  @override
  Future<StageState> stage() async {
    stages++;
    if (failFirst && stages == 1) throw StateError('unavailable');
    return const StageState(name: 's', revision: 3, home: 'h');
  }
}

class _GatedClient extends FakeSceneClient {
  final gates = <Completer<StageState>>[];

  @override
  Future<StageState> stage() {
    final gate = Completer<StageState>();
    gates.add(gate);
    return gate.future;
  }
}

class _MissingSceneClient extends FakeSceneClient {
  @override
  Future<StageState> stage() async => const StageState(
    name: 's',
    revision: 3,
    home: 'home',
    stack: [
      StageEntry(sceneKey: 'home'),
      StageEntry(sceneKey: 'gone'),
    ],
  );

  @override
  Future<SceneState> read(String sceneKey) async {
    if (sceneKey == 'gone') {
      throw UiRequestException('GET', 'scenes/gone', 404, '');
    }
    return super.read(sceneKey);
  }
}
