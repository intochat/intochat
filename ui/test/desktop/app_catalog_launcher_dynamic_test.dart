import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/app_catalog_launcher.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  group('AllAppsLauncherOverlay dynamic server-supplied catalog entries', () {
    testWidgets(
      'renders unseen server-supplied app.custom-tracker with title, category, fallback icon, and launches it',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(1440, 900));
        addTearDown(() => tester.binding.setSurfaceSize(null));

        final dynamicEntry = SmartAppTileSpec.fromIconSpec(
          'app.custom-tracker',
          titleOverride: 'Custom Tracker',
          categoryOverride: 'Workspace',
        );

        final baseApps = [
          SmartAppTileSpec.fromIconSpec('ino'),
          SmartAppTileSpec.fromIconSpec('files'),
          SmartAppTileSpec.fromIconSpec('settings'),
        ];

        String? launched;
        await tester.pumpWidget(
          MaterialApp(
            theme: UiTheme.dark('fluent-acrylic'),
            home: Scaffold(
              body: AllAppsLauncherOverlay(
                apps: [...baseApps, dynamicEntry],
                onLaunchApp: (id) => launched = id,
                onClose: () {},
              ),
            ),
          ),
        );
        await tester.pumpAndSettle();

        // Filter to Workspace so the unseen entry is clearly in view.
        await tester.tap(find.byKey(const ValueKey('all-apps-cat-Workspace')));
        await tester.pumpAndSettle();

        final tile = find.byKey(
          const ValueKey('all-apps-tile-app.custom-tracker'),
        );
        expect(tile, findsOneWidget);
        expect(
          find.descendant(of: tile, matching: find.text('Custom Tracker')),
          findsOneWidget,
        );
        expect(
          find.descendant(of: tile, matching: find.text('Workspace')),
          findsOneWidget,
        );
        expect(
          find.descendant(of: tile, matching: find.byType(IcAppIcon)),
          findsOneWidget,
        );

        await tester.tap(tile);
        await tester.pumpAndSettle();
        expect(launched, 'app.custom-tracker');
      },
    );

    testWidgets(
      'surfaces unseen server-supplied categories in the category filter bar',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(1440, 900));
        addTearDown(() => tester.binding.setSurfaceSize(null));

        final baseApps = [
          SmartAppTileSpec.fromIconSpec('ino'),
          SmartAppTileSpec.fromIconSpec('files'),
        ];

        final customCategoryEntry = SmartAppTileSpec.fromIconSpec(
          'app.field-ops',
          titleOverride: 'Field Ops',
          categoryOverride: 'Operations',
        );

        await tester.pumpWidget(
          MaterialApp(
            theme: UiTheme.dark('fluent-acrylic'),
            home: Scaffold(
              body: AllAppsLauncherOverlay(
                apps: [...baseApps, customCategoryEntry],
                onLaunchApp: (_) {},
                onClose: () {},
              ),
            ),
          ),
        );
        await tester.pumpAndSettle();

        final opsCategoryBtn = find.byKey(
          const ValueKey('all-apps-cat-Operations'),
        );
        expect(opsCategoryBtn, findsOneWidget);

        await tester.ensureVisible(opsCategoryBtn);
        await tester.pumpAndSettle();
        await tester.tap(opsCategoryBtn);
        await tester.pumpAndSettle();

        final tile = find.byKey(const ValueKey('all-apps-tile-app.field-ops'));
        expect(tile, findsOneWidget);
        expect(
          find.descendant(of: tile, matching: find.text('Field Ops')),
          findsOneWidget,
        );
        expect(
          find.descendant(of: tile, matching: find.text('Operations')),
          findsOneWidget,
        );
      },
    );
  });
}
