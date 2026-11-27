import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/desktop_window_controller.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

DesktopWindowProjection _win(
  String id,
  String appId,
  String title, {
  String brain = 'brain-1',
  bool pinned = false,
}) => DesktopWindowProjection(
  id: id,
  appId: appId,
  title: title,
  viewportUri: '$brain/apps/desktop/window/$id',
  tabs: [
    DesktopTabProjection(
      id: '$id/0',
      appId: appId,
      title: title,
      selected: true,
    ),
  ],
  activeTabId: '$id/0',
  isPinned: pinned,
  isFavourite: false,
);

void main() {
  group('DesktopWindowController', () {
    test('syncs from BrainSessionController outside build, respects AppLaunch.maximized, and disposes removed windows', () async {
      var projection = DesktopShellProjection(
        windows: [
          _win('w-1', 'ino', 'Ino'),
          _win('w-2', 'photocraft', 'PhotoCraft'),
        ],
        focusedViewId: 'w-1/0',
        pins: const ['ino'],
        tiles: const [
          DesktopAppTile(
            id: 'ino',
            title: 'Ino',
            icon: 'ino',
            category: 'Core',
            dock: 1,
          ),
          DesktopAppTile(
            id: 'photocraft',
            title: 'PhotoCraft',
            icon: 'photocraft',
            category: 'ArtCraft Studio',
            launch: AppLaunch.maximized,
            dock: 2,
          ),
        ],
        openWidgets: const [],
      );

      final session = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) async => projection,
        onAction: (_) async => const {'delivered': true},
      );
      addTearDown(session.dispose);

      final controller = DesktopWindowController(session: session);
      addTearDown(controller.dispose);

      await session.reloadDesktop();

      expect(controller.windowStates.keys, containsAll(['w-1', 'w-2']));
      expect(controller.stateFor('w-1')!.zoomed, isFalse);
      expect(controller.stateFor('w-2')!.zoomed, isTrue);
      expect(controller.topWindow?.id, 'w-1');
      expect(controller.mruWindows.map((w) => w.id).toList(), ['w-1', 'w-2']);

      // Update title & close w-2
      final removedState = controller.stateFor('w-2')!;
      projection = DesktopShellProjection(
        windows: [_win('w-1', 'ino', 'Ino Assistant')],
        focusedViewId: 'w-1/0',
        pins: const ['ino'],
        tiles: projection.tiles,
        openWidgets: const [],
      );
      await session.reloadDesktop();

      expect(controller.windowStates.keys, ['w-1']);
      expect(controller.stateFor('w-1')!.title, 'Ino Assistant');
      expect(controller.stateFor('w-2'), isNull);
      // Disposed ChangeNotifier throws on addListener
      expect(() => removedState.addListener(() {}), throwsFlutterError);
    });

    test('keeps pinned windows above unpinned windows and tracks MRU & showDesktopOnly', () async {
      final sentSignals = <String>[];
      final projection = DesktopShellProjection(
        windows: [
          _win('w-1', 'ino', 'Ino'),
          _win('w-2', 'files', 'Files'),
          _win('w-3', 'tasks', 'Tasks'),
        ],
        focusedViewId: 'w-1/0',
        pins: const ['ino'],
        tiles: const [],
        openWidgets: const [],
      );

      final session = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) async => projection,
        onAction: (event) async {
          if (event['signal'] case final String sig) sentSignals.add(sig);
          return const {'delivered': true};
        },
      );
      addTearDown(session.dispose);

      final controller = DesktopWindowController(session: session);
      addTearDown(controller.dispose);

      await session.reloadDesktop();

      // Pin w-2 (Files) -> moves above unpinned windows.
      controller.stateFor('w-2')!.setPinned(true);
      controller.normalizeZOrder();
      expect(controller.zOrder.last, 'w-2');

      // Focusing unpinned w-3 promotes it among unpinned, but pinned w-2 stays on top.
      controller.focusWindow(projection.windows[2]);
      expect(controller.zOrder, ['w-1', 'w-3', 'w-2']);
      expect(controller.mruWindows.map((w) => w.id).toList(), [
        'w-2',
        'w-3',
        'w-1',
      ]);
      expect(sentSignals, contains('desktop.focus'));

      // Minimizing top window updates topWindow to next visible window.
      controller.toggleMinimized('w-2');
      expect(controller.topWindow?.id, 'w-3');

      // showDesktopOnly hides all visible windows.
      controller.toggleShowDesktop();
      expect(controller.showDesktopOnly, isTrue);
      expect(controller.visibleWindows, isEmpty);
      expect(controller.topWindow, isNull);

      // Focusing a window clears showDesktopOnly and unminimizes it.
      controller.focusWindow(projection.windows[1]);
      expect(controller.showDesktopOnly, isFalse);
      expect(controller.stateFor('w-2')!.minimized, isFalse);
      expect(controller.topWindow?.id, 'w-2');
    });

    test(
      'autoArrange tiles unpinned windows while locking pinned bounds',
      () async {
        final projection = DesktopShellProjection(
          windows: [
            _win('w-1', 'ino', 'Ino'),
            _win('w-2', 'files', 'Files'),
            _win('w-3', 'tasks', 'Tasks'),
          ],
          focusedViewId: 'w-1/0',
          pins: const [],
          tiles: const [],
          openWidgets: const [],
        );

        final session = BrainSessionController(
          brainId: 'brain-1',
          loadProjection: (_) async => projection,
        );
        addTearDown(session.dispose);

        final controller = DesktopWindowController(session: session);
        addTearDown(controller.dispose);
        await session.reloadDesktop();

        const lockedRect = Rect.fromLTWH(500, 120, 420, 320);
        controller.stateFor('w-2')!
          ..setPinned(true)
          ..setBounds(lockedRect);
        controller.normalizeZOrder();

        controller.autoArrange(const Size(1280, 720));

        expect(controller.stateFor('w-2')!.bounds, lockedRect);
        final unpinned = [
          controller.stateFor('w-1')!.bounds,
          controller.stateFor('w-3')!.bounds,
        ]..sort((a, b) => a.left.compareTo(b.left));
        expect(unpinned[0].left, 24.0);
        expect(unpinned[0].top, 24.0);
        expect(unpinned[1].left, greaterThan(unpinned[0].right));
        expect(unpinned[1].top, 24.0);
      },
    );

    test('tears down window states cleanly on brain switch', () async {
      final session = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (brain) async => DesktopShellProjection(
          windows: [
            if (brain == 'brain-1')
              _win('w-1', 'ino', 'Ino', brain: brain)
            else
              _win('w-9', 'files', 'Files', brain: brain),
          ],
          focusedViewId: brain == 'brain-1' ? 'w-1/0' : 'w-9/0',
          pins: const [],
          tiles: const [],
          openWidgets: const [],
        ),
        desktopEnabled: true,
      );
      addTearDown(session.dispose);

      final controller = DesktopWindowController(session: session);
      addTearDown(controller.dispose);

      await session.reloadDesktop();
      final oldState = controller.stateFor('w-1')!;
      controller.setShowDesktopOnly(true);

      session.switchBrain('brain-2', desktopEnabled: true);
      await session.reloadDesktop();

      expect(() => oldState.addListener(() {}), throwsFlutterError);
      expect(controller.showDesktopOnly, isFalse);
      expect(controller.windowStates.keys, ['w-9']);
      expect(
        controller.stateFor('w-9')!.neuronUri,
        'neuron://brain-2/apps/files',
      );
    });

    testWidgets(
      'LiveDesktopShell build performs zero window-controller mutations and reads memoized catalogs',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(1280, 800));
        addTearDown(() => tester.binding.setSurfaceSize(null));

        final session = BrainSessionController(
          brainId: 'brain-1',
          loadProjection: (_) async => DesktopShellProjection(
            windows: [_win('w-1', 'ino', 'Ino')],
            focusedViewId: 'w-1/0',
            pins: const ['ino'],
            tiles: const [
              DesktopAppTile(
                id: 'ino',
                title: 'Ino',
                icon: 'ino',
                category: 'Core',
                dock: 1,
              ),
            ],
            openWidgets: const [],
          ),
          loadNode: (kind, name) async => switch (kind) {
            'viewport' => {
              'revision': 1,
              'root': {'kind': 'view', 'name': 'brain-1/apps/ino/view'},
            },
            _ => {
              'generation': 1,
              'view': {
                'title': 'Ino',
                'root': {'kind': 'text', 'value': 'Hello'},
              },
            },
          },
          watchNode: (_, _) => const Stream.empty(),
          desktopEnabled: true,
        );
        addTearDown(session.dispose);

        final controller = DesktopWindowController(session: session);
        addTearDown(controller.dispose);

        Widget buildTree(IcStylePreset preset) => BrainSessionScope(
          controller: session,
          child: MaterialApp(
            theme: UiTheme.dark('fluent-acrylic'),
            home: Scaffold(
              body: LiveDesktopShell(
                stylePreset: preset,
                barLayout: BarLayout.floatingGlass,
                paletteOpen: false,
                onTogglePalette: () {},
                onSelectPreset: (_) {},
                onCycleBarLayout: () {},
                windowController: controller,
              ),
            ),
          ),
        );

        await tester.pumpWidget(buildTree(IcStylePreset.fluentAcrylic));
        await tester.pump(const Duration(milliseconds: 50));

        var controllerNotifications = 0;
        controller.addListener(() => controllerNotifications++);

        final initialLauncher = session.launcherApps;
        final initialDock = session.dockApps;

        // Trigger multiple build passes with varying layout constraints & presets
        // to verify build() performs zero window-controller mutations.
        await tester.pumpWidget(buildTree(IcStylePreset.spatialGlass));
        await tester.pump(const Duration(milliseconds: 50));
        await tester.pumpWidget(buildTree(IcStylePreset.warmPaper));
        await tester.pump(const Duration(milliseconds: 50));

        expect(controllerNotifications, 0);
        expect(identical(session.launcherApps, initialLauncher), isTrue);
        expect(identical(session.dockApps, initialDock), isTrue);
      },
    );
  });
}
