import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';
import 'package:intochat_os_flutter_shell/desktop/os_bar.dart';
import 'package:intochat_os_flutter_shell/desktop/window_frame.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/workspace/workspace_app.dart';

Map<String, dynamic> _fakeDesktopView() => {
  'generation': 1,
  'view': {
    'root': {
      'kind': 'column',
      'children': [
        {
          'kind': 'row',
          'children': [
            {
              'kind': 'button',
              'key': 'dock-ino',
              'label': 'Ino',
              'enabled': true,
              'command': {'name': 'open', 'args': 'ino'},
            },
            {
              'kind': 'button',
              'key': 'dock-files',
              'label': 'Files',
              'enabled': true,
              'command': {'name': 'open', 'args': 'files'},
            },
          ],
        },
        {
          'kind': 'column',
          'children': [
            {
              'kind': 'row',
              'children': [
                {
                  'kind': 'button',
                  'key': 'focus-1',
                  'label': 'Ino',
                  'enabled': true,
                },
                {
                  'kind': 'button',
                  'key': 'close-1',
                  'label': 'Close',
                  'enabled': true,
                },
              ],
            },
            {
              'kind': 'neuronRef',
              'refKind': 'viewport',
              'neuronUri': 'test-brain/apps/desktop/window/win-1',
            },
          ],
        },
      ],
    },
  },
};

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
        child: MaterialApp(
          theme: UiTheme.light('warm-paper'),
          home: Scaffold(
            body: LiveDesktopShell(
              brain: 'test-brain',
              name: 'test-brain/apps/desktop/view',
              load: (kind, name) async {
                if (name.endsWith('/apps/desktop/view')) {
                  return _fakeDesktopView();
                }
                return _fakeWindowView();
              },
              watch: null,
              onAction: (_) async => const <String, dynamic>{},
              onActivate: (_) {},
              stylePreset: IcStylePreset.warmPaper,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
              knowsApp: (_) => true,
            ),
          ),
        ),
      ),
    ),
  );
}

void main() {
  testWidgets(
    'Compact (390x844) renders bottom UiAdaptiveNavBar, spacious density, no WindowFrame, and sheet overlays',
    (tester) async {
      const size = Size(390, 844);
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(_buildShellHarness(surfaceSize: size));
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

      await tester.pumpWidget(_buildShellHarness(surfaceSize: size));
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

      await tester.pumpWidget(_buildShellHarness(surfaceSize: size));
      await tester.pumpAndSettle();

      expect(find.byType(OsBar), findsOneWidget);
      expect(find.byType(WindowFrame), findsOneWidget);
      expect(find.byType(UiAdaptiveNavBar), findsNothing);
    },
  );
}
