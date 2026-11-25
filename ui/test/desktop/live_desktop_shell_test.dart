import 'dart:async';

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';
import 'package:intochat_os_flutter_shell/desktop/os_bar.dart';
import 'package:intochat_os_flutter_shell/desktop/presence_layer.dart';
import 'package:intochat_os_flutter_shell/desktop/smart_desktop.dart';
import 'package:intochat_os_flutter_shell/workspace/spatial_app_canvas.dart';
import 'package:intochat_os_flutter_shell/desktop/window_frame.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  // The desktop view as CompiledAppsHost.EnsureDesktopViewAsync publishes it: window frames (a chrome row, the tab
  // bar, the window's viewport reference) above the dock.
  Map<String, dynamic> buildSampleDesktopTree() =>
      desktopViewJson(windows: const {'w-1': 'Ino', 'w-2': 'Files'});

  testWidgets(
    'LiveDesktopShell renders SmartDesktop, floating Acrylic WindowFrames, and OsBar',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final actions = <Map<String, dynamic>>[];
      final activations = <Map<String, dynamic>>[];

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: LiveDesktopShell(
              knowsApp: (app) => app == 'ino' || app == 'files',
              brain: 'brain-1',
              name: 'brain-1/apps/desktop/view',
              load: (kind, name) async {
                if (kind == 'viewport') return viewportJson(name);
                if (name == 'brain-1/apps/desktop/view') {
                  return buildSampleDesktopTree();
                }
                return {
                  'generation': 1,
                  'view': {
                    'title': name,
                    'root': {'kind': 'text', 'value': 'Viewport: $name'},
                  },
                };
              },
              watch: (kind, name) => const Stream.empty(),
              onAction: (event) async => actions.add(event),
              requestApp: (app, operation, input) async => actions.add({
                'action': '$operation:${input.isEmpty ? app : input}',
              }),
              onActivate: (action) => activations.add(action),
              stylePreset: IcStylePreset.fluentAcrylic,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
              onOpenCompute: () {},
            ),
          ),
        ),
      );

      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      expect(find.byType(SmartDesktop), findsOneWidget);
      expect(find.byType(WindowFrame), findsNWidgets(2));
      expect(find.byType(OsBar), findsOneWidget);
      expect(find.text('Viewport: brain-1/apps/ino/view'), findsOneWidget);
      expect(find.text('Viewport: brain-1/apps/files/view'), findsOneWidget);

      // Clicking 3D Brain button in OsBar minimizes all windows to show 3D neural desktop
      await tester.tap(find.text('3D Brain'));
      await tester.pump();
      expect(find.byType(WindowFrame), findsNothing);

      // Clicking 3D Brain again restores windows
      await tester.tap(find.text('3D Brain'));
      await tester.pump();
      expect(find.byType(WindowFrame), findsNWidgets(2));
    },
  );

  testWidgets(
    'OsBar popovers (Clock, Tokens, Notifications), Right-Click Dock Menu, and Alt+` Window Switcher work end-to-end',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      var computeOpened = false;
      final actions = <Map<String, dynamic>>[];

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: LiveDesktopShell(
              knowsApp: (app) => app == 'ino' || app == 'files',
              brain: 'brain-1',
              name: 'brain-1/apps/desktop/view',
              load: (kind, name) async {
                if (kind == 'viewport') return viewportJson(name);
                if (name == 'brain-1/apps/desktop/view') {
                  return buildSampleDesktopTree();
                }
                return {
                  'generation': 1,
                  'view': {
                    'title': name,
                    'root': {'kind': 'text', 'value': 'Viewport: $name'},
                  },
                };
              },
              watch: (kind, name) => kind == 'ino.presence'
                  ? StreamController<void>().stream
                  : const Stream.empty(),
              // Smart mode on, Ino waiting and two approvals pending.
              read: (contract, key, method) async => switch (method) {
                'Read' when key == 'settings' => {
                  'variables': [
                    {'key': 'ino.smart', 'value': 'on'},
                  ],
                },
                'Read' => {'state': 'Waiting', 'revision': 3},
                'Pending' => {
                  'value': [{}, {}],
                },
                _ => null,
              },
              onAction: (event) async => actions.add(event),
              requestApp: (app, operation, input) async => actions.add({
                'action': '$operation:${input.isEmpty ? app : input}',
              }),
              onActivate: (_) {},
              stylePreset: IcStylePreset.fluentAcrylic,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
              onOpenCompute: () => computeOpened = true,
            ),
          ),
        ),
      );

      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));
      expect(find.byType(PresenceLayer), findsOneWidget);
      expect(find.byKey(const ValueKey('os-bar-presence-dot')), findsOneWidget);
      expect(
        find.descendant(
          of: find.byKey(const ValueKey('os-bar-notifications')),
          matching: find.text('2'),
        ),
        findsOneWidget,
      );

      // 1. Clock Popover
      await tester.tap(find.byKey(const ValueKey('os-bar-clock')));
      await tester.pump();
      expect(
        find.byKey(const ValueKey('desktop-popover-clock')),
        findsOneWidget,
      );

      // 2. Tokens & Compute Popover
      await tester.tap(find.byKey(const ValueKey('os-bar-compute')));
      await tester.pump();
      expect(
        find.byKey(const ValueKey('desktop-popover-tokens')),
        findsOneWidget,
      );
      expect(find.text('Tokens today'), findsOneWidget);
      await tester.tap(
        find.byKey(const ValueKey('popover-tokens-open-compute')),
      );
      await tester.pump();
      expect(computeOpened, isTrue);

      // 3. Notifications Popover
      await tester.tap(find.byKey(const ValueKey('os-bar-notifications')));
      await tester.pump();
      expect(
        find.byKey(const ValueKey('desktop-popover-notifications')),
        findsOneWidget,
      );
      expect(
        find.textContaining('Needs you · Approve Nordlys close date'),
        findsOneWidget,
      );
      await tester.tap(
        find.byKey(const ValueKey('popover-notifications-mark-read')),
      );
      await tester.pump();
      expect(
        find.byKey(const ValueKey('desktop-popover-notifications')),
        findsNothing,
      );

      // 4. Right-click Dock Context Menu
      await tester.tap(
        find.byKey(const ValueKey('dock-item-ino')),
        buttons: kSecondaryMouseButton,
      );
      await tester.pump();
      expect(
        find.byKey(const ValueKey('desktop-popover-dock-menu')),
        findsOneWidget,
      );
      expect(find.text('New window'), findsOneWidget);
      expect(find.text('Unpin from Dock'), findsOneWidget);
      await tester.tap(find.byKey(const ValueKey('dock-menu-pin')));
      await tester.pump();
      expect(
        find.byKey(const ValueKey('desktop-popover-dock-menu')),
        findsNothing,
      );

      // 5. Alt+` (⌥`) Visual Window Switcher
      await tester.sendKeyDownEvent(LogicalKeyboardKey.altLeft);
      await tester.sendKeyDownEvent(LogicalKeyboardKey.backquote);
      await tester.pump();
      expect(
        find.byKey(const ValueKey('desktop-window-switcher')),
        findsOneWidget,
      );
      await tester.sendKeyUpEvent(LogicalKeyboardKey.backquote);
      await tester.sendKeyUpEvent(LogicalKeyboardKey.altLeft);
      await tester.pump();
      expect(
        find.byKey(const ValueKey('desktop-window-switcher')),
        findsNothing,
      );
    },
  );

  testWidgets(
    'SmartDesktop whiteboard supports sticky-note dragging, resizing, pinning, and Auto-Arrange locking pinned cards',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: const Scaffold(body: SmartDesktop()),
        ),
      );
      await tester.pump();

      final state = tester.state<SmartDesktopState>(find.byType(SmartDesktop));
      final initialLauncher = state.boundsForCard('launcher')!;
      final initialSignal = state.boundsForCard('signal')!;

      // 1. Drag 'launcher' sticky-note card to a custom position
      await tester.drag(
        find.byKey(const ValueKey('sticky-card-drag-launcher')),
        const Offset(120, 80),
      );
      await tester.pump();
      final draggedLauncher = state.boundsForCard('launcher')!;
      expect(draggedLauncher.left, greaterThan(initialLauncher.left));
      expect(draggedLauncher.top, greaterThan(initialLauncher.top));

      // 2. Resize 'launcher' sticky-note card via bottom-right corner handle
      await tester.drag(
        find.byKey(const ValueKey('sticky-card-resize-launcher')),
        const Offset(50, 40),
      );
      await tester.pump();
      final resizedLauncher = state.boundsForCard('launcher')!;
      expect(resizedLauncher.width, greaterThan(draggedLauncher.width));
      expect(resizedLauncher.height, greaterThan(draggedLauncher.height));

      // 3. Pin 'launcher' card
      expect(state.isCardPinned('launcher'), isFalse);
      await tester.tap(find.byKey(const ValueKey('sticky-card-pin-launcher')));
      await tester.pump();
      expect(state.isCardPinned('launcher'), isTrue);

      // 4. Drag unpinned 'signal' card away from its slot
      await tester.drag(
        find.byKey(const ValueKey('sticky-card-drag-signal')),
        const Offset(240, 140),
      );
      await tester.pump();
      final draggedSignal = state.boundsForCard('signal')!;
      expect(draggedSignal.left, isNot(equals(initialSignal.left)));

      // 5. Click 'Auto-Arrange' -> pinned 'launcher' Rect is strictly locked, unpinned 'signal' moves to grid
      await tester.tap(
        find.byKey(const ValueKey('whiteboard-auto-arrange-btn')),
      );
      await tester.pump();

      expect(state.boundsForCard('launcher'), equals(resizedLauncher));
      expect(state.boundsForCard('signal'), isNot(equals(draggedSignal)));
      await tester.pump(kitToastLifetime);
    },
  );

  testWidgets(
    'WindowFrame top bar supports pin always-on-top, reload (<500ms) vs hard-restart (>500ms), and execution mode settings popover',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final actions = <Map<String, dynamic>>[];
      final activations = <Map<String, dynamic>>[];
      var viewportLoads = 0;

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: LiveDesktopShell(
              knowsApp: (app) => app == 'ino' || app == 'files',
              brain: 'brain-1',
              name: 'brain-1/apps/desktop/view',
              load: (kind, name) async {
                if (kind == 'viewport') return viewportJson(name);
                if (name == 'brain-1/apps/desktop/view') {
                  return buildSampleDesktopTree();
                }
                if (name == 'brain-1/apps/files/view') {
                  viewportLoads++;
                }
                return {
                  'generation': 1,
                  'view': {
                    'title': name,
                    'root': {'kind': 'text', 'value': 'Viewport: $name'},
                  },
                };
              },
              watch: (kind, name) => const Stream.empty(),
              onAction: (event) async => actions.add(event),
              requestApp: (app, operation, input) async => actions.add({
                'action': '$operation:${input.isEmpty ? app : input}',
              }),
              onActivate: (action) => activations.add(action),
              stylePreset: IcStylePreset.fluentAcrylic,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
            ),
          ),
        ),
      );

      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      // 1. Short click (<500ms) on Reload button refreshes the window's ViewHost
      final loadsBefore = viewportLoads;
      await tester.tap(find.byKey(const ValueKey('window-reload-btn-2')));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));
      expect(viewportLoads, greaterThan(loadsBefore));
      expect(
        actions.where((a) => '${a['action']}'.startsWith('sandbox-restart')),
        isEmpty,
      );

      // 2. Long hold (>500ms) on Reload button triggers hard sandbox restart
      final reloadCenter = tester.getCenter(
        find.byKey(const ValueKey('window-reload-btn-2')),
      );
      final gesture = await tester.startGesture(reloadCenter);
      await tester.pump(const Duration(milliseconds: 550));
      await gesture.up();
      await tester.pump();

      expect(
        actions.any((a) => a['action'] == 'sandbox-restart:files'),
        isTrue,
      );
      expect(find.text('Sandbox Restarted'), findsOneWidget);
      await tester.pump(kitToastLifetime);

      // 3. Settings Popover toggles and switches Execution Mode (wasm-ui, mcp, native-linux)
      await tester.tap(find.byKey(const ValueKey('window-settings-btn-2')));
      await tester.pump();
      expect(
        find.byKey(const ValueKey('window-settings-popover-2')),
        findsOneWidget,
      );
      expect(find.byKey(const ValueKey('exec-mode-wasm-ui')), findsOneWidget);
      expect(find.byKey(const ValueKey('exec-mode-mcp')), findsOneWidget);
      expect(
        find.byKey(const ValueKey('exec-mode-native-linux')),
        findsOneWidget,
      );

      await tester.tap(find.byKey(const ValueKey('exec-mode-mcp')));
      await tester.pump();
      expect(actions.any((a) => a['action'] == 'switch-mode:mcp'), isTrue);
      expect(find.text('Mode: MCP Server'), findsOneWidget);
      await tester.pump(kitToastLifetime);

      // 4. Pinning window 2 keeps it always-on-top even when window 1 is clicked/focused
      await tester.tap(find.byKey(const ValueKey('window-pin-btn-2')));
      await tester.pump();
      // Click on Window 1's exposed left area to focus Window 1
      await tester.tapAt(const Offset(560, 200));
      await tester.pump();
      final framesAfterPin = tester
          .widgetList<WindowFrame>(find.byType(WindowFrame))
          .toList();
      expect(framesAfterPin.last.window.id, 2);
      expect(framesAfterPin.last.window.isPinned, isTrue);
    },
  );

  testWidgets(
    'Workspace app opens SpatialAppCanvas in a window, displays cards with UnifiedAppHeader, and supports Smart Sort',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final actions = <Map<String, dynamic>>[];

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: LiveDesktopShell(
              knowsApp: (app) => app == 'ino' || app == 'files',
              brain: 'brain-1',
              name: 'brain-1/apps/desktop/view',
              load: (kind, name) async {
                if (kind == 'viewport') return viewportJson(name);
                if (name == 'brain-1/apps/desktop/view') {
                  return buildSampleDesktopTree();
                }
                return {
                  'generation': 1,
                  'view': {
                    'title': name,
                    'root': {'kind': 'text', 'value': 'Viewport: $name'},
                  },
                };
              },
              watch: (kind, name) => const Stream.empty(),
              onAction: (event) async => actions.add(event),
              requestApp: (app, operation, input) async => actions.add({
                'action': '$operation:${input.isEmpty ? app : input}',
              }),
              onActivate: (_) {},
              stylePreset: IcStylePreset.fluentAcrylic,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
            ),
          ),
        ),
      );

      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      // Initially 2 windows open, no SpatialAppCanvas, and closed apps are not in the active-apps dock
      expect(find.byType(SpatialAppCanvas), findsNothing);
      expect(find.byType(WindowFrame), findsNWidgets(2));
      expect(find.byKey(const ValueKey('dock-item-workspace')), findsNothing);

      // 1. Launch Workspace app from the All Apps launcher
      await tester.tap(find.byKey(const ValueKey('os-bar-all-apps')));
      await tester.pump();
      await tester.tap(find.byKey(const ValueKey('all-apps-tile-workspace')));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      expect(find.byType(SpatialAppCanvas), findsOneWidget);
      expect(find.byType(WindowFrame), findsNWidgets(3));
      expect(find.byKey(const ValueKey('dock-item-workspace')), findsOneWidget);
      expect(
        find.byKey(const ValueKey('spatial-card-pos-w-1')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('spatial-card-pos-w-2')),
        findsOneWidget,
      );

      // 2. Canvas HUD controls are available
      expect(
        find.byKey(const ValueKey('canvas-smart-sort-btn')),
        findsOneWidget,
      );
      expect(find.byKey(const ValueKey('canvas-zoom-in')), findsOneWidget);
      expect(find.byKey(const ValueKey('canvas-zoom-out')), findsOneWidget);
      expect(find.byKey(const ValueKey('canvas-reset-camera')), findsOneWidget);

      // 3. Trigger Smart Sort
      await tester.tap(find.byKey(const ValueKey('canvas-smart-sort-btn')));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 600));
    },
  );

  testWidgets(
    'Clicking App settings in dock menu or window titlebar opens the Creator graph focused on that app',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final actions = <Map<String, dynamic>>[];
      final activations = <Map<String, dynamic>>[];

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: LiveDesktopShell(
              knowsApp: (app) => app == 'ino' || app == 'files',
              brain: 'brain-1',
              name: 'brain-1/apps/desktop/view',
              load: (kind, name) async {
                if (kind == 'viewport') return viewportJson(name);
                if (name == 'brain-1/apps/desktop/view') {
                  return buildSampleDesktopTree();
                }
                return {
                  'generation': 1,
                  'view': {
                    'title': name,
                    'root': {'kind': 'text', 'value': 'Viewport: $name'},
                  },
                };
              },
              watch: (kind, name) => const Stream.empty(),
              onAction: (event) async => actions.add(event),
              requestApp: (app, operation, input) async => actions.add({
                'action': '$operation:${input.isEmpty ? app : input}',
              }),
              onActivate: (action) => activations.add(action),
              stylePreset: IcStylePreset.fluentAcrylic,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
            ),
          ),
        ),
      );

      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      expect(find.byType(WindowFrame), findsNWidgets(2));
      expect(find.byKey(const ValueKey('dock-item-photocraft')), findsNothing);

      // Open PhotoCraft via All Apps launcher so it appears in the active-apps dock
      await tester.tap(find.byKey(const ValueKey('os-bar-all-apps')));
      await tester.pump();
      await tester.tap(find.byKey(const ValueKey('all-apps-tile-photocraft')));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));
      expect(find.byType(WindowFrame), findsNWidgets(3));
      expect(
        find.text('Viewport: brain-1/apps/photocraft/view'),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('dock-item-photocraft')),
        findsOneWidget,
      );

      // Right-click on PhotoCraft in the active-apps dock and click "App settings…"
      await tester.tap(
        find.byKey(const ValueKey('dock-item-photocraft')),
        buttons: kSecondaryMouseButton,
      );
      await tester.pump();
      expect(find.byKey(const ValueKey('dock-menu-settings')), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('dock-menu-settings')));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      // The Creator opens immediately with brain-1/apps/photocraft selected in its graph
      expect(find.byType(WindowFrame), findsNWidgets(4));
      expect(
        activations.any(
          (a) =>
              a['application'] == 'creator' &&
              a['operation'] == 'open' &&
              a['input'] == 'node:brain-1/apps/photocraft',
        ),
        isTrue,
      );
    },
  );

  testWidgets(
    'LiveDesktopShell opens no windows or widgets by default when state is empty, and persists widget/window open & close actions',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final actions = <Map<String, dynamic>>[];
      final emptyTree = desktopViewJson(windows: const {});

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: LiveDesktopShell(
              knowsApp: (app) => app == 'ino' || app == 'files',
              brain: 'brain-1',
              name: 'brain-1/apps/desktop/view',
              load: (kind, name) async => emptyTree,
              watch: null,
              onAction: (action) async => actions.add(action),
              requestApp: (app, operation, input) async => actions.add({
                'action': '$operation:${input.isEmpty ? app : input}',
              }),
              onActivate: (_) {},
              stylePreset: IcStylePreset.fluentAcrylic,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
            ),
          ),
        ),
      );

      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      // No windows or sticky-note widgets open by default
      expect(find.byType(WindowFrame), findsNothing);
      expect(find.byKey(const ValueKey('sticky-card-launcher')), findsNothing);
      expect(find.byKey(const ValueKey('sticky-card-signal')), findsNothing);
      expect(find.byKey(const ValueKey('sticky-card-identity')), findsNothing);
      expect(find.byKey(const ValueKey('sticky-card-apps')), findsNothing);

      // Open Widgets menu and toggle Ino Launcher widget
      await tester.tap(find.byKey(const ValueKey('whiteboard-widgets-btn')));
      await tester.pump();
      await tester.tap(
        find.byKey(const ValueKey('whiteboard-widget-toggle-launcher')),
      );
      await tester.pump();
      expect(
        find.byKey(const ValueKey('sticky-card-launcher')),
        findsOneWidget,
      );
      expect(actions.any((a) => a['action'] == 'widget-open:launcher'), isTrue);

      // Close the sticky-note widget via its X button
      await tester.tap(
        find.byKey(const ValueKey('sticky-card-close-launcher')),
      );
      await tester.pump();
      expect(find.byKey(const ValueKey('sticky-card-launcher')), findsNothing);
      expect(
        actions.any((a) => a['action'] == 'widget-close:launcher'),
        isTrue,
      );
    },
  );

  testWidgets(
    'the seeded desktop view renders its windows and dock from the typed nodes',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      final events = <Map<String, dynamic>>[];
      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: LiveDesktopShell(
              knowsApp: (app) => app == 'ino' || app == 'files',
              brain: 'brain-1',
              name: 'brain-1/apps/desktop/view',
              load: (kind, name) async => switch (kind) {
                'viewport' => viewportJson(name),
                _ when name == 'brain-1/apps/desktop/view' =>
                  buildSampleDesktopTree(),
                _ => {
                  'generation': 1,
                  'view': {
                    'title': name,
                    'root': {'kind': 'text', 'value': 'Viewport: $name'},
                  },
                },
              },
              watch: (kind, name) => const Stream.empty(),
              onAction: (event) async => events.add(event),
              onActivate: (_) {},
              stylePreset: IcStylePreset.fluentAcrylic,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
            ),
          ),
        ),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));
      expect(tester.takeException(), isNull);
      expect(find.byType(WindowFrame), findsNWidgets(2));
      expect(find.text('Viewport: brain-1/apps/ino/view'), findsOneWidget);
      expect(find.text('Viewport: brain-1/apps/files/view'), findsOneWidget);
      expect(find.text('This component cannot be displayed.'), findsNothing);
      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'an OpenWindow input reaches the app as plain text, never as a URL',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      final activations = <Map<String, dynamic>>[];
      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: LiveDesktopShell(
              knowsApp: (app) => app == 'files' || app == 'ino',
              brain: 'brain-1',
              name: 'brain-1/apps/desktop/view',
              load: (kind, name) async => switch (kind) {
                'viewport' => viewportJson(name),
                _ when name == 'brain-1/apps/desktop/view' =>
                  buildSampleDesktopTree(),
                _ => {
                  'generation': 1,
                  'view': {
                    'title': name,
                    'root': {
                      'kind': 'button',
                      'key': 'open-${name.split('/')[2]}',
                      'label': 'Open ${name.split('/')[2]}',
                      'intent': {'intentKind': 'OpenWindow', 'arg': ''},
                    },
                  },
                },
              },
              watch: (kind, name) => const Stream.empty(),
              onAction: (event) async => {
                'intent': {
                  'intentKind': 'OpenWindow',
                  'arg': 'os://window/open?app=files&input=javascript:alert(1)&url=https://evil.example',
                },
              },
              onActivate: activations.add,
              stylePreset: IcStylePreset.fluentAcrylic,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
            ),
          ),
        ),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));
      await tester.tap(find.text('Open files'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      final opened = activations
          .where((a) => a['application'] == 'files')
          .toList();
      expect(opened, isNotEmpty);
      expect(opened.last['operation'], 'open');
      expect(opened.last['input'], 'javascript:alert(1)');
      expect(activations.any((a) => '$a'.contains('evil.example')), isFalse);
      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'All Apps Launcher lists server-supplied ArtCraft AppTileNodes and opens selected studio app window maximized',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final activations = <Map<String, dynamic>>[];

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: LiveDesktopShell(
              knowsApp: (_) => true,
              brain: 'brain-1',
              name: 'brain-1/apps/desktop/view',
              load: (kind, name) async {
                if (kind == 'viewport') return viewportJson(name);
                if (name == 'brain-1/apps/desktop/view') {
                  return buildSampleDesktopTree();
                }
                return {
                  'generation': 1,
                  'view': {
                    'title': name,
                    'root': {'kind': 'text', 'value': 'Viewport: $name'},
                  },
                };
              },
              watch: (kind, name) => const Stream.empty(),
              onAction: (_) async => null,
              onActivate: activations.add,
              stylePreset: IcStylePreset.fluentAcrylic,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
            ),
          ),
        ),
      );

      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      // Click the All Apps button in the OS Bar
      await tester.tap(find.byKey(const ValueKey('os-bar-all-apps')));
      await tester.pump();

      expect(
        find.byKey(const ValueKey('all-apps-launcher-overlay')),
        findsOneWidget,
      );

      // Filter by ArtCraft Studio category (derived from server AppTileNode catalog)
      await tester.tap(
        find.byKey(const ValueKey('all-apps-cat-ArtCraft Studio')),
      );
      await tester.pump();

      expect(
        find.byKey(const ValueKey('all-apps-tile-vectorcraft')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('all-apps-tile-solidcraft')),
        findsOneWidget,
      );

      // Launch VectorCraft from the launcher
      await tester.tap(find.byKey(const ValueKey('all-apps-tile-vectorcraft')));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));

      expect(
        find.byKey(const ValueKey('all-apps-launcher-overlay')),
        findsNothing,
      );
      expect(
        activations.any(
          (a) => a['application'] == 'vectorcraft' && a['operation'] == 'open',
        ),
        isTrue,
      );

      // Verify server-declared AppLaunch.maximized opened the window zoomed
      final frames = tester
          .widgetList<WindowFrame>(find.byType(WindowFrame))
          .where((f) => f.window.appId == 'vectorcraft')
          .toList();
      expect(frames, hasLength(1));
      expect(frames.single.window.zoomed, isTrue);
    },
  );
}

const _windowApps = {'w-1': 'ino', 'w-2': 'files'};

/// A desktop window's viewport: the app view it shows.
Map<String, dynamic> viewportJson(String name) {
  final window = name.substring(name.lastIndexOf('/') + 1);
  return {
    'revision': 1,
    'root': {
      'kind': 'view',
      'name': 'brain-1/apps/${_windowApps[window] ?? window}/view',
    },
  };
}

Map<String, dynamic> _button(
  String key,
  String label,
  String signal, [
  String? argument,
  String? icon,
]) => {
  'kind': 'button',
  'key': key,
  'label': label,
  'emit': {'owner': 'desktop', 'signal': signal, 'args': ?argument},
  'enabled': true,
  'tone': 'neutral',
  'style': 'secondary',
  'icon': ?icon,
  'small': icon != null,
};

Map<String, dynamic> _appTile(
  String key,
  String appId,
  String title, {
  String? icon,
  String? category,
  String launch = 'windowed',
  int? dock,
  String appKind = 'compiled',
}) => {
  'kind': 'appTile',
  'key': key,
  'appId': appId,
  'title': title,
  'icon': ?icon,
  'category': ?category,
  'launch': launch,
  'dock': ?dock,
  'appKind': appKind,
  'command': {'owner': 'desktop', 'signal': 'open', 'args': appId},
};

/// The seeded desktop view (`{generation, view}`) with one frame per window id → title.
Map<String, dynamic> desktopViewJson({required Map<String, String> windows}) {
  Map<String, dynamic> frame(String id, String title) => {
    'kind': 'column',
    'gap': 0,
    'padding': 0,
    'extents': [40, 38, 0],
    'children': [
      {
        'kind': 'row',
        'gap': 4,
        'padding': 0,
        'extents': [0, 32, 32, 32, 32],
        'children': [
          _button('focus-$id', title, 'focus', id),
          _button('newtab-$id', 'New tab', 'newtab', id, 'plus'),
          _button('star-$id', 'Add to favourites', 'favourite', id, 'star'),
          _button(
            'pin-$id',
            'Pin',
            'pin',
            _windowApps[id] ?? title.toLowerCase(),
            'pin',
          ),
          _button('close-$id', 'Close tab', 'close', '$id/0', 'x'),
        ],
      },
      {
        'kind': 'neuronRef',
        'refKind': 'slot',
        'neuronUri':
            'brain-1/control.tabStrip/brain-1/app.desktop/default/tabs-$id',
        'props': {
          'items': [
            {'id': '$id/0', 'label': title},
          ],
          'selectedId': '$id/0',
        },
      },
      {
        'kind': 'neuronRef',
        'refKind': 'viewport',
        'neuronUri': 'brain-1/apps/desktop/window/$id',
      },
    ],
  };
  return {
    'generation': 1,
    'view': {
      'title': 'Desktop',
      'root': {
        'kind': 'column',
        'gap': 12,
        'padding': 0,
        'extents': [0, 68],
        'children': [
          {
            'kind': 'column',
            'gap': 12,
            'padding': 0,
            'children': [
              if (windows.isNotEmpty)
                {
                  'kind': 'row',
                  'gap': 12,
                  'padding': 0,
                  'children': [
                    for (final MapEntry(:key, :value) in windows.entries)
                      frame(key, value),
                  ],
                },
            ],
          },
          {
            'kind': 'row',
            'gap': 12,
            'padding': 0,
            'extents': [0, 360],
            'children': [
              {
                'kind': 'row',
                'gap': 4,
                'padding': 0,
                'children': [
                  _appTile(
                    'dock-ino',
                    'ino',
                    'Ino',
                    icon: 'ino',
                    category: 'Core',
                    dock: 1,
                  ),
                  _appTile(
                    'dock-files',
                    'files',
                    'Files',
                    icon: 'files',
                    category: 'Workspace',
                    dock: 2,
                  ),
                  _appTile(
                    'dock-gallery',
                    'gallery',
                    'UI Gallery',
                    icon: 'gallery',
                    category: 'System',
                    dock: 8,
                  ),
                  _button('newtab', 'New tab', 'newtab', null, 'plus'),
                ],
              },
              {
                'kind': 'row',
                'gap': 4,
                'padding': 0,
                'children': [
                  {'kind': 'text', 'value': 'Usage'},
                  {'kind': 'text', 'value': ''},
                  _button('avatar', 'You', 'open', 'circle'),
                  _button('more-settings', 'Settings', 'settings'),
                ],
              },
            ],
          },
          {
            'kind': 'surface',
            'name': 'app-catalog',
            'padding': 0,
            'children': [
              _appTile(
                'catalog-ino',
                'ino',
                'Ino',
                icon: 'ino',
                category: 'Core',
                dock: 1,
              ),
              _appTile(
                'catalog-files',
                'files',
                'Files',
                icon: 'files',
                category: 'Workspace',
                dock: 2,
              ),
              _appTile(
                'catalog-gallery',
                'gallery',
                'UI Gallery',
                icon: 'gallery',
                category: 'System',
                dock: 8,
              ),
              _appTile(
                'catalog-settings',
                'settings',
                'Settings',
                icon: 'settings',
                category: 'System',
                dock: 10,
              ),
              _appTile(
                'catalog-creator',
                'creator',
                'Creator',
                icon: 'creator',
                category: 'System',
                dock: 7,
              ),
              _appTile(
                'catalog-workspace',
                'workspace',
                'Workspace',
                icon: 'workspace',
                category: 'Workspace',
              ),
              _appTile(
                'catalog-photocraft',
                'photocraft',
                'PhotoCraft',
                icon: 'photocraft',
                category: 'ArtCraft Studio',
                launch: 'maximized',
                dock: 5,
              ),
              _appTile(
                'catalog-vectorcraft',
                'vectorcraft',
                'VectorCraft',
                icon: 'vectorcraft',
                category: 'ArtCraft Studio',
                launch: 'maximized',
              ),
              _appTile(
                'catalog-solidcraft',
                'solidcraft',
                'SolidCraft',
                icon: 'solidcraft',
                category: 'ArtCraft Studio',
                launch: 'maximized',
              ),
            ],
          },
        ],
      },
    },
  };
}
