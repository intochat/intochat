import 'package:digitalbrain_flutter_shell/space/neuron_space.dart';
import 'package:digitalbrain_flutter_shell/space/space_controller.dart';
import 'package:digitalbrain_flutter_shell/space/space_slots.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flame/game.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'space_fixture.dart';

void main() {
  Future<(SpaceClient, SpaceScenes, SpaceController)> mount(
    WidgetTester tester, {
    bool reduced = false,
    bool side = false,
    String? error,
  }) async {
    final ui = SpaceClient();
    final scenes = SpaceScenes(ui)
      ..side = side
      ..error = error;
    final controller = SpaceController(
      client: ui.client,
      scenes: scenes,
      brain: 'brain',
      watch: () => ui.changes.stream,
    );
    await controller.start();
    await tester.pumpWidget(
      MaterialApp(
        home: MediaQuery(
          data: MediaQueryData(disableAnimations: reduced),
          child: Scaffold(
            body: NeuronSpace(
              controller: controller,
              load: (kind, name) async => kind == 'text'
                  ? {
                      'markdown': name.endsWith('/side')
                          ? 'Side content'
                          : 'Main content',
                    }
                  : await loadSlot(kind, name),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    addTearDown(() async {
      await tester.pumpWidget(const SizedBox());
      controller.dispose();
      await ui.changes.close();
      ui.close();
    });
    return (ui, scenes, controller);
  }

  testWidgets(
    'a fresh brain shows Ino focused with its main slot and the Home button',
    (tester) async {
      await mount(tester);
      expect(find.byType(SideSlot), findsNothing);
      expect(find.byType(MainSlot), findsOneWidget);
      expect(find.byTooltip('Home'), findsOneWidget);
      expect(find.text('Main content'), findsOneWidget);
    },
  );
  testWidgets('tapping a sphere posts Show and the slots swap after the zoom', (
    tester,
  ) async {
    final (ui, scenes, controller) = await mount(tester);
    final game = tester
        .widget<GameWidget<SceneGame>>(find.byType(GameWidget<SceneGame>))
        .game!;
    scenes.revision++;
    game.simulateTap('brain/apps/files');
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));
    await tester.pumpAndSettle();
    expect(ui.calls.single.key, 'brain/apps/files');
    expect(scenes.reports, ['brain/apps/files']);
    expect(controller.view.value.focused, 'brain/apps/files');
  });
  testWidgets('Home pops to the brain', (tester) async {
    final (ui, scenes, _) = await mount(tester);
    scenes.revision++;
    await tester.tap(find.byTooltip('Home'));
    await tester.pumpAndSettle();
    expect(ui.workspace.path, ['brain']);
    expect(find.byType(MainSlot), findsNothing);
  });
  testWidgets('Home reverses the camera flight over 420 milliseconds', (
    tester,
  ) async {
    final (_, scenes, controller) = await mount(tester);
    final game = tester
        .widget<GameWidget<SceneGame>>(find.byType(GameWidget<SceneGame>))
        .game!;
    expect(game.state!.definition.camera.x, 176);
    scenes.revision++;
    await controller.home();
    await tester.pump();
    expect(game.state!.definition.camera.x, 176);
    await tester.pump(const Duration(milliseconds: 210));
    expect(
      game.state!.definition.camera.x,
      closeTo(176 * (1 - Curves.easeInOutCubic.transform(.5)), 0.1),
    );
    await tester.pump(const Duration(milliseconds: 210));
    expect(game.state!.definition.camera.x, 0);
  });
  testWidgets('reduced motion cuts the flight', (tester) async {
    final (_, scenes, controller) = await mount(tester, reduced: true);
    scenes.revision++;
    await controller.show('brain/apps/files');
    await tester.pump();
    expect(find.byType(MainSlot), findsOneWidget);
    final game = tester
        .widget<GameWidget<SceneGame>>(find.byType(GameWidget<SceneGame>))
        .game!;
    expect(game.state!.definition.camera.x, 320 + 800 * .22);
    expect(
      game.componentFor('brain/apps/files')!.scale.x,
      closeTo(2.2, 0.00001),
    );
    for (final transition in tester.widgetList<FadeTransition>(
      find.byType(FadeTransition),
    )) {
      expect(transition.opacity.value, 1);
    }
  });
  testWidgets('each slot kind renders in its region', (tester) async {
    await mount(tester, side: true);
    expect(
      find.descendant(
        of: find.byType(SideSlot),
        matching: find.text('Side content'),
      ),
      findsOneWidget,
    );
    expect(
      find.descendant(
        of: find.byType(MainSlot),
        matching: find.text('Main content'),
      ),
      findsOneWidget,
    );
    expect(tester.getSize(find.byType(SideSlot)).width, 320);
  });
  testWidgets('an errored presentation shows attention and the error in main', (
    tester,
  ) async {
    await mount(tester, error: 'Could not prepare app');
    expect(find.bySemanticsLabel('Needs attention'), findsOneWidget);
    expect(
      find.descendant(
        of: find.byType(MainSlot),
        matching: find.text('Could not prepare app'),
      ),
      findsOneWidget,
    );
  });
}
