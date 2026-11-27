import 'dart:async';

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';
import 'package:intochat_os_flutter_shell/desktop/os_bar.dart';
import 'package:intochat_os_flutter_shell/desktop/presence_layer.dart';
import 'package:intochat_os_flutter_shell/desktop/smart_desktop.dart';
import 'package:intochat_os_flutter_shell/desktop/window_frame.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/workspace/spatial_app_canvas.dart';

/// Stateful fake desktop projection backend mirroring server `DesktopApp`
/// transitions without any client-side shadow window state.
class FakeDesktopBackend {
  FakeDesktopBackend({
    this.brain = 'brain-1',
    Map<String, String> initialWindows = const {'w-1': 'ino', 'w-2': 'files'},
    List<String> initialPins = const ['ino', 'files'],
    List<String> initialWidgets = const [],
    List<DesktopAppTile>? tiles,
  }) : _pins = List<String>.from(initialPins),
       _openWidgets = List<String>.from(initialWidgets),
       _tiles = tiles ?? defaultCatalogTiles() {
    var idx = 0;
    for (final entry in initialWindows.entries) {
      final appId = entry.value.toLowerCase();
      final title = _titleFor(appId);
      final tabId = '${entry.key}/0';
      _windows.add(
        DesktopWindowProjection(
          id: entry.key,
          appId: appId,
          title: title,
          viewportUri: '$brain/apps/desktop/window/${entry.key}',
          tabs: [
            DesktopTabProjection(
              id: tabId,
              appId: appId,
              title: title,
              selected: true,
            ),
          ],
          activeTabId: tabId,
          isPinned: _pins.contains(appId),
          isFavourite: false,
        ),
      );
      _windowApps[entry.key] = appId;
      _focusedViewId = tabId;
      idx++;
    }
    _nextId = idx + 1;
  }

  final String brain;
  final List<DesktopWindowProjection> _windows = [];
  final List<String> _pins;
  final List<String> _openWidgets;
  final List<DesktopAppTile> _tiles;
  final Map<String, String> _windowApps = {};
  String? _focusedViewId;
  int _nextId = 1;

  String _titleFor(String appId) {
    for (final t in _tiles) {
      if (t.id.toLowerCase() == appId.toLowerCase()) return t.title;
    }
    return IcAppIcon.humanizeAppTitle(appId);
  }

  DesktopShellProjection snapshot() => DesktopShellProjection(
    windows: List.unmodifiable(_windows),
    focusedViewId: _focusedViewId,
    pins: List.unmodifiable(_pins),
    tiles: List.unmodifiable(_tiles),
    openWidgets: List.unmodifiable(_openWidgets),
  );

  ViewportPayload viewportFor(String uri) {
    final winId = uri.substring(uri.lastIndexOf('/') + 1);
    final appId = _windowApps[winId] ?? winId;
    return ViewportPayload(
      revision: 1,
      root: UiViewRef(kind: 'view', name: '$brain/apps/$appId/view'),
    );
  }

  Map<String, dynamic> handleAction(UiAction event) {
    if (event is! ActAction) return const {'delivered': true};
    final signal = event.signal;
    final args = (event.args as Map?)?.cast<String, dynamic>() ?? const {};
    switch (signal) {
      case 'desktop.open':
        final appId = '${args['app'] ?? ''}'.trim().toLowerCase();
        if (appId.isNotEmpty) _openApp(appId);
      case 'desktop.settings':
        _openApp('settings');
      case 'desktop.focus':
        final winId = '${args['windowId'] ?? ''}';
        for (final w in _windows) {
          if (w.id == winId) {
            _focusedViewId = w.activeTabId;
            break;
          }
        }
      case 'desktop.close':
        final target = '${args['target'] ?? ''}';
        final winId = target.split('/').first;
        _windows.removeWhere((w) => w.id == winId);
      case 'desktop.close-app':
        final appId = '${args['app'] ?? ''}'.toLowerCase();
        _windows.removeWhere((w) => w.appId.toLowerCase() == appId);
      case 'desktop.pin':
        final appId = '${args['appId'] ?? ''}'.toLowerCase();
        if (appId.isNotEmpty && !_pins.contains(appId)) _pins.add(appId);
      case 'desktop.unpin':
        final appId = '${args['appId'] ?? ''}'.toLowerCase();
        _pins.remove(appId);
      case 'desktop.widget-open':
        final w = '${args['widget'] ?? ''}'.trim().toLowerCase();
        if (w.isNotEmpty && !_openWidgets.contains(w)) _openWidgets.add(w);
      case 'desktop.widget-close':
        final w = '${args['widget'] ?? ''}'.trim().toLowerCase();
        _openWidgets.remove(w);
    }
    return const {'delivered': true};
  }

  void _openApp(String appId) {
    final existing = _windows.where((w) => w.appId == appId).firstOrNull;
    if (existing != null) {
      _focusedViewId = existing.activeTabId;
      return;
    }
    final winId = 'w-${_nextId++}';
    final tabId = '$winId/0';
    final title = _titleFor(appId);
    _windowApps[winId] = appId;
    _windows.add(
      DesktopWindowProjection(
        id: winId,
        appId: appId,
        title: title,
        viewportUri: '$brain/apps/desktop/window/$winId',
        tabs: [
          DesktopTabProjection(
            id: tabId,
            appId: appId,
            title: title,
            selected: true,
          ),
        ],
        activeTabId: tabId,
        isPinned: _pins.contains(appId),
        isFavourite: false,
      ),
    );
    _focusedViewId = tabId;
  }

  static List<DesktopAppTile> defaultCatalogTiles() => const [
    DesktopAppTile(
      id: 'ino',
      title: 'Ino',
      icon: 'ino',
      category: 'Core',
      dock: 1,
    ),
    DesktopAppTile(
      id: 'files',
      title: 'Files',
      icon: 'files',
      category: 'Workspace',
      dock: 2,
    ),
    DesktopAppTile(
      id: 'gallery',
      title: 'UI Gallery',
      icon: 'gallery',
      category: 'System',
      dock: 8,
    ),
    DesktopAppTile(
      id: 'settings',
      title: 'Settings',
      icon: 'settings',
      category: 'System',
      dock: 10,
    ),
    DesktopAppTile(
      id: 'creator',
      title: 'Creator',
      icon: 'creator',
      category: 'System',
      dock: 7,
    ),
    DesktopAppTile(
      id: 'workspace',
      title: 'Workspace',
      icon: 'workspace',
      category: 'Workspace',
    ),
    DesktopAppTile(
      id: 'photocraft',
      title: 'PhotoCraft',
      icon: 'photocraft',
      category: 'ArtCraft Studio',
      launch: AppLaunch.maximized,
      dock: 5,
    ),
    DesktopAppTile(
      id: 'vectorcraft',
      title: 'VectorCraft',
      icon: 'vectorcraft',
      category: 'ArtCraft Studio',
      launch: AppLaunch.maximized,
    ),
    DesktopAppTile(
      id: 'solidcraft',
      title: 'SolidCraft',
      icon: 'solidcraft',
      category: 'ArtCraft Studio',
      launch: AppLaunch.maximized,
    ),
  ];
}

void main() {
  Widget wrapShell(
    BrainSessionController session, {
    VoidCallback? onOpenCompute,
  }) => BrainSessionScope(
    controller: session,
    child: MaterialApp(
      theme: UiTheme.dark('fluent-acrylic'),
      home: Scaffold(
        body: LiveDesktopShell(
          stylePreset: IcStylePreset.fluentAcrylic,
          barLayout: BarLayout.floatingGlass,
          paletteOpen: false,
          onTogglePalette: () {},
          onSelectPreset: (_) {},
          onCycleBarLayout: () {},
          onOpenCompute: onOpenCompute,
        ),
      ),
    ),
  );

  testWidgets(
    'LiveDesktopShell renders SmartDesktop, floating Acrylic WindowFrames, and OsBar from typed projection',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final backend = FakeDesktopBackend();
      final actions = <Map<String, dynamic>>[];
      final activations = <Map<String, dynamic>>[];
      final session = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) async => backend.snapshot(),
        loadNode: (kind, name) async {
          if (kind == 'viewport') return backend.viewportFor(name);
          return PublishedViewPayload.fromJson({
            'generation': 1,
            'view': {
              'title': name,
              'root': {'kind': 'text', 'value': 'Viewport: $name'},
            },
          });
        },
        watchNode: (_, _) => const Stream.empty(),
        onAction: (event) async {
          actions.add(event.toJson());
          return backend.handleAction(event);
        },
        requestApp: (app, operation, input) async => actions.add({
          'action': '$operation:${input.isEmpty ? app : input}',
        }),
        onActivate: activations.add,
        knowsApp: (app) => app == 'ino' || app == 'files',
      );
      addTearDown(session.dispose);

      await tester.pumpWidget(wrapShell(session, onOpenCompute: () {}));
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

      final backend = FakeDesktopBackend();
      var computeOpened = false;
      final actions = <Map<String, dynamic>>[];
      final presenceController = StreamController<void>.broadcast();
      addTearDown(presenceController.close);

      final session = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) async => backend.snapshot(),
        loadNode: (kind, name) async {
          if (kind == 'viewport') return backend.viewportFor(name);
          return PublishedViewPayload.fromJson({
            'generation': 1,
            'view': {
              'title': name,
              'root': {'kind': 'text', 'value': 'Viewport: $name'},
            },
          });
        },
        watchNode: (kind, name) => kind == 'ino.presence'
            ? presenceController.stream
            : const Stream.empty(),
        readContract: (contract, key, method) async => switch (method) {
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
        onAction: (event) async {
          actions.add(event.toJson());
          return backend.handleAction(event);
        },
        requestApp: (app, operation, input) async => actions.add({
          'action': '$operation:${input.isEmpty ? app : input}',
        }),
        onActivate: (_) {},
        knowsApp: (app) => app == 'ino' || app == 'files',
      );
      addTearDown(session.dispose);

      await tester.pumpWidget(
        wrapShell(session, onOpenCompute: () => computeOpened = true),
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
        find.byKey(const ValueKey('popover-notifications-empty')),
        findsOneWidget,
      );
      expect(find.text('No notifications yet'), findsOneWidget);
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

      // 5. Alt+` Visual Window Switcher
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
          home: const Scaffold(body: SmartDesktop()),
        ),
      );
      await tester.pump();

      final state = tester.state<SmartDesktopState>(find.byType(SmartDesktop));
      final initialLauncher = state.boundsForCard('launcher')!;
      final initialSignal = state.boundsForCard('signal')!;

      await tester.drag(
        find.byKey(const ValueKey('sticky-card-drag-launcher')),
        const Offset(120, 80),
      );
      await tester.pump();
      final draggedLauncher = state.boundsForCard('launcher')!;
      expect(draggedLauncher.left, greaterThan(initialLauncher.left));
      expect(draggedLauncher.top, greaterThan(initialLauncher.top));

      await tester.drag(
        find.byKey(const ValueKey('sticky-card-resize-launcher')),
        const Offset(50, 40),
      );
      await tester.pump();
      final resizedLauncher = state.boundsForCard('launcher')!;
      expect(resizedLauncher.width, greaterThan(draggedLauncher.width));
      expect(resizedLauncher.height, greaterThan(draggedLauncher.height));

      expect(state.isCardPinned('launcher'), isFalse);
      await tester.tap(find.byKey(const ValueKey('sticky-card-pin-launcher')));
      await tester.pump();
      expect(state.isCardPinned('launcher'), isTrue);

      await tester.drag(
        find.byKey(const ValueKey('sticky-card-drag-signal')),
        const Offset(240, 140),
      );
      await tester.pump();
      final draggedSignal = state.boundsForCard('signal')!;
      expect(draggedSignal.left, isNot(equals(initialSignal.left)));

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

      final backend = FakeDesktopBackend();
      final actions = <Map<String, dynamic>>[];
      var viewportLoads = 0;

      final session = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) async => backend.snapshot(),
        loadNode: (kind, name) async {
          if (kind == 'viewport') return backend.viewportFor(name);
          if (name == 'brain-1/apps/files/view') {
            viewportLoads++;
          }
          return PublishedViewPayload.fromJson({
            'generation': 1,
            'view': {
              'title': name,
              'root': {'kind': 'text', 'value': 'Viewport: $name'},
            },
          });
        },
        watchNode: (_, _) => const Stream.empty(),
        onAction: (event) async {
          actions.add(event.toJson());
          return backend.handleAction(event);
        },
        requestApp: (app, operation, input) async => actions.add({
          'action': '$operation:${input.isEmpty ? app : input}',
        }),
        onActivate: (_) {},
        knowsApp: (app) => app == 'ino' || app == 'files',
      );
      addTearDown(session.dispose);

      await tester.pumpWidget(wrapShell(session));
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

      // 3. Settings Popover toggles and switches Execution Mode
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

      final backend = FakeDesktopBackend();
      final session = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) async => backend.snapshot(),
        loadNode: (kind, name) async {
          if (kind == 'viewport') return backend.viewportFor(name);
          return PublishedViewPayload.fromJson({
            'generation': 1,
            'view': {
              'title': name,
              'root': {'kind': 'text', 'value': 'Viewport: $name'},
            },
          });
        },
        watchNode: (_, _) => const Stream.empty(),
        onAction: (event) async => backend.handleAction(event),
        onActivate: (_) {},
        knowsApp: (app) => app == 'ino' || app == 'files',
      );
      addTearDown(session.dispose);

      await tester.pumpWidget(wrapShell(session));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

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

      expect(
        find.byKey(const ValueKey('canvas-smart-sort-btn')),
        findsOneWidget,
      );
      expect(find.byKey(const ValueKey('canvas-zoom-in')), findsOneWidget);
      expect(find.byKey(const ValueKey('canvas-zoom-out')), findsOneWidget);
      expect(find.byKey(const ValueKey('canvas-reset-camera')), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('canvas-smart-sort-btn')));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 600));
    },
  );

  testWidgets(
    'Clicking App settings in dock menu opens the Creator graph focused on that app',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final backend = FakeDesktopBackend();
      final activations = <Map<String, dynamic>>[];
      final session = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) async => backend.snapshot(),
        loadNode: (kind, name) async {
          if (kind == 'viewport') return backend.viewportFor(name);
          return PublishedViewPayload.fromJson({
            'generation': 1,
            'view': {
              'title': name,
              'root': {'kind': 'text', 'value': 'Viewport: $name'},
            },
          });
        },
        watchNode: (_, _) => const Stream.empty(),
        onAction: (event) async => backend.handleAction(event),
        onActivate: activations.add,
        knowsApp: (app) => app == 'ino' || app == 'files',
      );
      addTearDown(session.dispose);

      await tester.pumpWidget(wrapShell(session));
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
    'LiveDesktopShell opens no windows or widgets when projection is empty, and renders multi-widget openWidgets without separator bugs',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final backend = FakeDesktopBackend(initialWindows: const {});
      final actions = <Map<String, dynamic>>[];
      final session = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) async => backend.snapshot(),
        loadNode: (kind, name) async =>
            const PublishedViewPayload(generation: 1),
        onAction: (action) async {
          actions.add(action.toJson());
          return backend.handleAction(action);
        },
        onActivate: (_) {},
        knowsApp: (app) => app == 'ino' || app == 'files',
      );
      addTearDown(session.dispose);

      await tester.pumpWidget(wrapShell(session));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      expect(find.byType(WindowFrame), findsNothing);
      expect(find.byKey(const ValueKey('sticky-card-launcher')), findsNothing);
      expect(find.byKey(const ValueKey('sticky-card-signal')), findsNothing);

      // Open Widgets menu and toggle Ino Launcher widget
      await tester.tap(find.byKey(const ValueKey('whiteboard-widgets-btn')));
      await tester.pump();
      await tester.tap(
        find.byKey(const ValueKey('whiteboard-widget-toggle-launcher')),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));

      expect(
        find.byKey(const ValueKey('sticky-card-launcher')),
        findsOneWidget,
      );
      expect(
        actions.any(
          (a) =>
              a['signal'] == 'desktop.widget-open' &&
              (a['args'] as Map?)?['widget'] == 'launcher',
        ),
        isTrue,
      );

      // Also open Signal widget -> both sticky cards render simultaneously
      await tester.tap(
        find.byKey(const ValueKey('whiteboard-widget-toggle-signal')),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      expect(
        find.byKey(const ValueKey('sticky-card-launcher')),
        findsOneWidget,
      );
      expect(find.byKey(const ValueKey('sticky-card-signal')), findsOneWidget);

      // Close Launcher widget via its X button
      await tester.tap(
        find.byKey(const ValueKey('sticky-card-close-launcher')),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      expect(find.byKey(const ValueKey('sticky-card-launcher')), findsNothing);
      expect(find.byKey(const ValueKey('sticky-card-signal')), findsOneWidget);
      expect(
        actions.any(
          (a) =>
              a['signal'] == 'desktop.widget-close' &&
              (a['args'] as Map?)?['widget'] == 'launcher',
        ),
        isTrue,
      );
    },
  );

  testWidgets(
    'an OpenWindow input reaches the app as plain text, never as a URL',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final backend = FakeDesktopBackend();
      final activations = <Map<String, dynamic>>[];
      final session = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) async => backend.snapshot(),
        loadNode: (kind, name) async => switch (kind) {
          'viewport' => backend.viewportFor(name),
          _ => PublishedViewPayload.fromJson({
            'generation': 1,
            'view': {
              'title': name,
              'root': {
                'kind': 'button',
                'key': 'open-${name.split('/')[2]}',
                'label': 'Open ${name.split('/')[2]}',
                'intent': {
                  'intentKind': 'OpenWindow',
                  'arg': 'os://window/open?app=files&input=javascript:alert(1)&url=https://evil.example',
                },
              },
            },
          }),
        },
        watchNode: (_, _) => const Stream.empty(),
        onAction: (event) async => backend.handleAction(event),
        onActivate: activations.add,
        knowsApp: (app) => app == 'files' || app == 'ino',
      );
      addTearDown(session.dispose);

      await tester.pumpWidget(wrapShell(session));
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
    'All Apps Launcher lists server-supplied ArtCraft tiles and opens selected studio app window maximized',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final backend = FakeDesktopBackend();
      final activations = <Map<String, dynamic>>[];
      final session = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) async => backend.snapshot(),
        loadNode: (kind, name) async {
          if (kind == 'viewport') return backend.viewportFor(name);
          return PublishedViewPayload.fromJson({
            'generation': 1,
            'view': {
              'title': name,
              'root': {'kind': 'text', 'value': 'Viewport: $name'},
            },
          });
        },
        watchNode: (_, _) => const Stream.empty(),
        onAction: (event) async => backend.handleAction(event),
        onActivate: activations.add,
        knowsApp: (_) => true,
      );
      addTearDown(session.dispose);

      await tester.pumpWidget(wrapShell(session));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      await tester.tap(find.byKey(const ValueKey('os-bar-all-apps')));
      await tester.pump();
      expect(
        find.byKey(const ValueKey('all-apps-launcher-overlay')),
        findsOneWidget,
      );

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

      final frames = tester
          .widgetList<WindowFrame>(find.byType(WindowFrame))
          .where((f) => f.window.appId == 'vectorcraft')
          .toList();
      expect(frames, hasLength(1));
      expect(frames.single.window.zoomed, isTrue);
    },
  );
}
