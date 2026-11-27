import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';
import 'package:intochat_os_flutter_shell/desktop/os_bar.dart';
import 'package:intochat_os_flutter_shell/desktop/window_frame.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/workspace/workspace_app.dart';

DesktopShellProjection _fakeProjection() => const DesktopShellProjection(
  windows: [
    DesktopWindowProjection(
      id: 'w-1',
      appId: 'ino',
      title: 'Ino',
      viewportUri: 'test-brain/apps/desktop/window/w-1',
      tabs: [
        DesktopTabProjection(
          id: 'w-1/0',
          appId: 'ino',
          title: 'Ino',
          selected: true,
        ),
      ],
      activeTabId: 'w-1/0',
      isPinned: true,
      isFavourite: false,
    ),
  ],
  focusedViewId: 'w-1/0',
  pins: ['ino', 'files'],
  tiles: [
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
  ],
  openWidgets: [],
);

Map<String, dynamic> _fakeWindowView() => {
  'generation': 1,
  'view': {
    'root': {
      'kind': 'column',
      'children': [
        {'kind': 'text', 'value': 'Hello from Ino'},
      ],
    },
  },
};

Widget _buildShellHarness({
  required Size surfaceSize,
  required BrainSessionController session,
  UiThemeSeeds seeds = UiThemeSeeds.warmPaper,
}) {
  return MediaQuery(
    data: MediaQueryData(size: surfaceSize),
    child: UiSeedThemeScope(
      seeds: seeds,
      child: UiThemeSeedsController(
        seeds: seeds,
        setSeeds: (_) {},
        resetSeeds: () {},
        child: BrainSessionScope(
          controller: session,
          child: MaterialApp(
            theme: UiTheme.light('warm-paper'),
            home: Scaffold(
              body: LiveDesktopShell(
                stylePreset: IcStylePreset.warmPaper,
                barLayout: BarLayout.floatingGlass,
                paletteOpen: false,
                onTogglePalette: () {},
                onSelectPreset: (_) {},
                onCycleBarLayout: () {},
              ),
            ),
          ),
        ),
      ),
    ),
  );
}

BrainSessionController _createSession() => BrainSessionController(
  brainId: 'test-brain',
  loadProjection: (_) async => _fakeProjection(),
  loadNode: (kind, name) async {
    if (kind == 'viewport') {
      return {
        'revision': 1,
        'root': {'kind': 'view', 'name': 'test-brain/apps/ino/view'},
      };
    }
    return _fakeWindowView();
  },
  onAction: (_) async => const <String, dynamic>{},
  onActivate: (_) {},
  knowsApp: (_) => true,
);

void main() {
  testWidgets(
    'Compact (390x844) renders bottom UiAdaptiveNavBar, spacious density, no WindowFrame, and sheet overlays',
    (tester) async {
      const size = Size(390, 844);
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final session = _createSession();
      addTearDown(session.dispose);

      await tester.pumpWidget(
        _buildShellHarness(surfaceSize: size, session: session),
      );
      await tester.pumpAndSettle();

      expect(
        find.byKey(const ValueKey('adaptive-shell-compact')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('ui_adaptive_nav_bottom')),
        findsOneWidget,
      );
      expect(find.byKey(const ValueKey('ui_adaptive_nav_rail')), findsNothing);
      expect(find.byType(WindowFrame), findsNothing);
      expect(find.byType(OsBar), findsNothing);

      final compactScope = tester.widget<UiSeedThemeScope>(
        find.byKey(const ValueKey('adaptive-compact-theme-scope')),
      );
      expect(compactScope.seeds.density, UiDensity.spacious);

      // Opening All Apps on compact presents a UiSheet bottom sheet.
      await tester.tap(find.byKey(const ValueKey('ui_nav_item_apps')));
      await tester.pumpAndSettle();
      expect(find.byType(UiSheet), findsOneWidget);
      expect(find.text('All Apps'), findsWidgets);

      // Dismiss All Apps sheet and open Theme Studio sheet.
      await tester.tapAt(const Offset(195, 40));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('ui_nav_item_theme')));
      await tester.pumpAndSettle();
      expect(find.byType(UiSheet), findsOneWidget);
      expect(find.byType(ThemeStudioPanel), findsOneWidget);
    },
  );

  testWidgets(
    'Medium (768x1024) renders left UiAdaptiveNavBar rail alongside focused content',
    (tester) async {
      const size = Size(768, 1024);
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final session = _createSession();
      addTearDown(session.dispose);

      await tester.pumpWidget(
        _buildShellHarness(surfaceSize: size, session: session),
      );
      await tester.pumpAndSettle();

      expect(
        find.byKey(const ValueKey('adaptive-shell-medium')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('ui_adaptive_nav_rail')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('ui_adaptive_nav_bottom')),
        findsNothing,
      );
      expect(find.byType(WindowFrame), findsNothing);
      expect(find.byType(OsBar), findsNothing);
    },
  );

  testWidgets(
    'Expanded (1280x800) renders desktop layout with OsBar and floating WindowFrame',
    (tester) async {
      const size = Size(1280, 800);
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final session = _createSession();
      addTearDown(session.dispose);

      await tester.pumpWidget(
        _buildShellHarness(surfaceSize: size, session: session),
      );
      await tester.pumpAndSettle();

      expect(find.byType(OsBar), findsOneWidget);
      expect(find.byType(WindowFrame), findsOneWidget);
      expect(find.byType(UiAdaptiveNavBar), findsNothing);
    },
  );
}
