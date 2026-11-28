import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/desktop_adaptive_shell.dart';
import 'package:intochat_os_flutter_shell/desktop/desktop_overlays.dart';
import 'package:intochat_os_flutter_shell/desktop/guest_status_pill.dart';
import 'package:intochat_os_flutter_shell/desktop/smart_desktop.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _filesAppTile = SmartAppTileSpec(
  id: 'files',
  name: 'Files',
  icon: LucideIcons.folder,
  color: Color(0xFF3B82F6),
  neuronUri: 'brain-1/apps/files',
  category: 'Workspace',
);

Widget _wrapOverlay(
  Widget child, {
  UiThemeSeeds seeds = UiThemeSeeds.warmPaper,
}) {
  return UiSeedThemeScope(
    seeds: seeds,
    child: UiThemeSeedsController(
      seeds: seeds,
      setSeeds: (_) {},
      resetSeeds: () {},
      child: MaterialApp(
        theme: UiTheme.light('warm-paper'),
        home: Scaffold(
          body: Stack(fit: StackFit.expand, children: [child]),
        ),
      ),
    ),
  );
}

void main() {
  group('resolveIsGuestSession', () {
    test('honors explicit overrides before client or try- prefix fallback', () {
      expect(resolveIsGuestSession(isGuest: true, brainId: 'brain-1'), isTrue);
      expect(
        resolveIsGuestSession(
          isGuest: false,
          isEphemeral: true,
          clientIsGuest: true,
          brainId: 'try-123',
        ),
        isFalse,
      );
      expect(
        resolveIsGuestSession(isEphemeral: true, brainId: 'brain-1'),
        isTrue,
      );
      expect(
        resolveIsGuestSession(
          isEphemeral: false,
          clientIsGuest: true,
          brainId: 'try-123',
        ),
        isFalse,
      );
      expect(
        resolveIsGuestSession(clientIsGuest: true, brainId: 'brain-1'),
        isTrue,
      );
      expect(resolveIsGuestSession(brainId: 'try-guest-99'), isTrue);
      expect(resolveIsGuestSession(brainId: 'brain-1'), isFalse);
      expect(resolveIsGuestSession(), isFalse);
    });
  });

  group('DesktopChromeOverlayHost', () {
    testWidgets(
      'owns guest download modal lifecycle, invokes before-open hook, and closes on Escape',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(1280, 800));
        addTearDown(() => tester.binding.setSurfaceSize(null));

        var beforeOpenCount = 0;
        var saveTapped = false;

        await tester.pumpWidget(
          _wrapOverlay(
            Positioned.fill(
              child: DesktopChromeOverlayHost(
                onBeforeOpenDownloadModal: () => beforeOpenCount++,
                guestChrome: DesktopGuestChromeConfig(
                  showPill: true,
                  remaining: const Duration(hours: 12),
                  onSaveBrain: () => saveTapped = true,
                  useSafeArea: true,
                ),
              ),
            ),
          ),
        );

        expect(find.byType(GuestStatusPill), findsOneWidget);
        expect(find.text('Guest Brain · Temp RAM · 12h'), findsOneWidget);
        expect(find.byType(DownloadAppModal), findsNothing);

        await tester.tap(find.byKey(const ValueKey('guest-pill-save-brain')));
        await tester.pump();
        expect(saveTapped, isTrue);

        await tester.tap(find.byKey(const ValueKey('guest-pill-download-app')));
        await tester.pump();
        expect(beforeOpenCount, 1);
        expect(find.byType(DownloadAppModal), findsOneWidget);

        await tester.sendKeyDownEvent(LogicalKeyboardKey.escape);
        await tester.pump();
        await tester.sendKeyUpEvent(LogicalKeyboardKey.escape);
        await tester.pump();
        expect(find.byType(DownloadAppModal), findsNothing);
      },
    );

    testWidgets(
      'renders All Apps, Theme Studio, Alt+` Switcher, and Popover layers cleanly',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(1280, 800));
        addTearDown(() => tester.binding.setSurfaceSize(null));

        String? launchedApp;
        DesktopWindowSummary? selectedWindow;
        var popoverClosed = false;

        await tester.pumpWidget(
          _wrapOverlay(
            Positioned.fill(
              child: DesktopChromeOverlayHost(
                popover: DesktopPopoverOverlayConfig(
                  kind: DesktopPopoverKind.clock,
                  screen: const Size(1280, 800),
                  barLayout: BarLayout.floatingGlass,
                  anchorX: 1100,
                  notificationsCount: 0,
                  notifications: const [],
                  notificationsLoaded: true,
                  notificationsLoading: false,
                  pinnedAppIds: const {'ino'},
                  windows: const [],
                  onClose: () => popoverClosed = true,
                  onOpenSettings: () {},
                  onOpenComputeSettings: () {},
                  onMarkNotificationRead: (_) {},
                  onMarkAllNotificationsRead: () {},
                  onClearNotifications: () {},
                  onFocusWindow: (_) {},
                  onNewWindowForApp: (_) {},
                  onTogglePinApp: (_) {},
                  onQuitApp: (_) {},
                ),
                windowSwitcher: DesktopWindowSwitcherConfig(
                  windows: const [
                    DesktopWindowSummary(
                      windowId: 'w-1',
                      numericId: 1,
                      appId: 'ino',
                      title: 'Ino',
                    ),
                  ],
                  selectedIndex: 0,
                  onSelectWindow: (w) => selectedWindow = w,
                  onCancel: () {},
                ),
                allAppsOpen: true,
                launcherApps: const [_filesAppTile],
                onCloseAllApps: () {},
                onLaunchApp: (appId) => launchedApp = appId,
                themeStudioOpen: true,
                themeStudioTop: 16,
                onCloseThemeStudio: () {},
              ),
            ),
          ),
        );
        await tester.pump();

        expect(
          find.byKey(const ValueKey('desktop-popover-clock')),
          findsOneWidget,
        );
        expect(
          find.byKey(const ValueKey('desktop-window-switcher')),
          findsOneWidget,
        );
        expect(find.byType(AllAppsLauncherOverlay), findsOneWidget);
        expect(find.byType(ThemeStudioPanel), findsOneWidget);
        expect(launchedApp, isNull);
        expect(selectedWindow, isNull);
        expect(popoverClosed, isFalse);
      },
    );
  });

  group('DesktopAdaptiveShell', () {
    const sampleWindow = DesktopWindowProjection(
      id: 'w-2',
      appId: 'files',
      title: 'Files',
      viewportUri: 'brain-1/apps/desktop/window/w-2',
      tabs: [
        DesktopTabProjection(
          id: 'w-2/0',
          appId: 'files',
          title: 'Files',
          selected: true,
        ),
      ],
      activeTabId: 'w-2/0',
      isPinned: true,
      isFavourite: false,
    );

    testWidgets(
      'Compact shell renders InoOrbV2 when non-Ino window is active, Active Apps sheet, and GuestStatusPill',
      (tester) async {
        const size = Size(390, 844);
        tester.view.physicalSize = size;
        tester.view.devicePixelRatio = 1.0;
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);

        String? focusedWindowId;
        String? launchedAppId;

        await tester.pumpWidget(
          MediaQuery(
            data: const MediaQueryData(size: size),
            child: _wrapOverlay(
              DesktopAdaptiveShell(
                mode: DesktopAdaptiveLayoutMode.compact,
                stylePreset: IcStylePreset.warmPaper,
                barLayout: BarLayout.floatingGlass,
                desk: const Size(390, 780),
                mruWindows: const [sampleWindow],
                visibleWindows: const [sampleWindow],
                topWindow: sampleWindow,
                dockApps: const [],
                launcherApps: const [_filesAppTile],
                openWidgets: const [],
                showDesktopOnly: false,
                allAppsOpen: false,
                themeStudioOpen: false,
                presenceState: PresenceState.idle,
                buildWindowViewHost: (w) => Center(child: Text('View:${w.id}')),
                onLaunchApp: (id) => launchedAppId = id,
                onToggleShowDesktop: () {},
                onFocusWindow: (id) => focusedWindowId = id,
                onCloseWindow: (_) {},
                onOpenWidget: (_) {},
                onCloseWidget: (_) {},
                onAutoArrange: () {},
                onTogglePalette: () {},
                onSelectPreset: (_) {},
                onCycleBarLayout: () {},
                onSetAllAppsOpen: (_) {},
                onSetThemeStudioOpen: (_) {},
                onClearOverlays: () {},
                guestChrome: const DesktopGuestChromeConfig(showPill: true),
              ),
            ),
          ),
        );
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 300));

        expect(
          find.byKey(const ValueKey('adaptive-shell-compact')),
          findsOneWidget,
        );
        expect(find.text('View:w-2'), findsOneWidget);
        expect(find.byType(GuestStatusPill), findsOneWidget);

        // Tap Active nav item -> opens Active Apps sheet and focuses selected window.
        await tester.tap(find.byKey(const ValueKey('ui_nav_item_active')));
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 350));
        expect(find.text('Active Apps'), findsOneWidget);
        await tester.tap(find.text('Files'));
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 350));
        expect(focusedWindowId, 'w-2');

        // Tap Ino nav item -> launches 'ino'.
        await tester.tap(find.byKey(const ValueKey('ui_nav_item_ino')));
        await tester.pump();
        expect(launchedAppId, 'ino');
      },
    );

    testWidgets(
      'Medium shell renders left nav rail, presence layer, modal overlays, and GuestStatusPill via DesktopChromeOverlayHost',
      (tester) async {
        const size = Size(768, 1024);
        tester.view.physicalSize = size;
        tester.view.devicePixelRatio = 1.0;
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);

        await tester.pumpWidget(
          MediaQuery(
            data: const MediaQueryData(size: size),
            child: _wrapOverlay(
              DesktopAdaptiveShell(
                mode: DesktopAdaptiveLayoutMode.medium,
                stylePreset: IcStylePreset.warmPaper,
                barLayout: BarLayout.floatingGlass,
                desk: const Size(768, 960),
                mruWindows: const [sampleWindow],
                visibleWindows: const [sampleWindow],
                topWindow: sampleWindow,
                dockApps: const [],
                launcherApps: const [_filesAppTile],
                openWidgets: const [],
                showDesktopOnly: false,
                allAppsOpen: true,
                themeStudioOpen: false,
                presenceState: PresenceState.idle,
                presenceLayer: const SizedBox(
                  key: ValueKey('medium-presence-probe'),
                ),
                buildWindowViewHost: (w) => Center(child: Text('View:${w.id}')),
                onLaunchApp: (_) {},
                onToggleShowDesktop: () {},
                onFocusWindow: (_) {},
                onCloseWindow: (_) {},
                onOpenWidget: (_) {},
                onCloseWidget: (_) {},
                onAutoArrange: () {},
                onTogglePalette: () {},
                onSelectPreset: (_) {},
                onCycleBarLayout: () {},
                onSetAllAppsOpen: (_) {},
                onSetThemeStudioOpen: (_) {},
                onClearOverlays: () {},
                guestChrome: const DesktopGuestChromeConfig(showPill: true),
              ),
            ),
          ),
        );
        await tester.pumpAndSettle();

        expect(
          find.byKey(const ValueKey('adaptive-shell-medium')),
          findsOneWidget,
        );
        expect(
          find.byKey(const ValueKey('medium-presence-probe')),
          findsOneWidget,
        );
        expect(find.byType(AllAppsLauncherOverlay), findsOneWidget);
        expect(find.byType(GuestStatusPill), findsOneWidget);
      },
    );
  });
}
