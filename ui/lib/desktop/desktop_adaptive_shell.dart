import 'dart:async';

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import '../src/widgets/ino/ino_orb_v2.dart';
import 'desktop_overlays.dart';
import 'smart_desktop.dart';

/// Width below which the desktop shell switches to the Compact mobile layout.
const double kDesktopCompactBreakpoint = 600.0;

/// Width at or above which the desktop shell switches to the Expanded floating
/// window manager layout.
const double kDesktopExpandedBreakpoint = 1024.0;

/// Adaptive single-pane shell mode for non-expanded viewports.
enum DesktopAdaptiveLayoutMode { compact, medium }

/// Compact (`<600px`) and Medium (`600–1024px`) adaptive desktop shells, plus
/// the Active Apps, All Apps, and Theme Studio modal bottom sheets.
class DesktopAdaptiveShell extends StatelessWidget {
  const DesktopAdaptiveShell({
    super.key,
    required this.mode,
    required this.stylePreset,
    required this.barLayout,
    required this.desk,
    required this.mruWindows,
    required this.visibleWindows,
    required this.topWindow,
    required this.dockApps,
    required this.launcherApps,
    required this.openWidgets,
    required this.showDesktopOnly,
    required this.allAppsOpen,
    required this.themeStudioOpen,
    required this.presenceState,
    required this.buildWindowViewHost,
    required this.onLaunchApp,
    required this.onToggleShowDesktop,
    required this.onFocusWindow,
    required this.onCloseWindow,
    required this.onOpenWidget,
    required this.onCloseWidget,
    required this.onAutoArrange,
    required this.onTogglePalette,
    required this.onSelectPreset,
    required this.onCycleBarLayout,
    required this.onSetAllAppsOpen,
    required this.onSetThemeStudioOpen,
    required this.onClearOverlays,
    required this.guestChrome,
    this.profileMenu,
    this.presenceLayer,
  });

  final DesktopAdaptiveLayoutMode mode;
  final IcStylePreset stylePreset;
  final BarLayout barLayout;
  final Size desk;
  final List<DesktopWindowProjection> mruWindows;
  final List<DesktopWindowProjection> visibleWindows;
  final DesktopWindowProjection? topWindow;
  final List<SmartAppTileSpec> dockApps;
  final List<SmartAppTileSpec> launcherApps;
  final List<String> openWidgets;
  final bool showDesktopOnly;
  final bool allAppsOpen;
  final bool themeStudioOpen;
  final PresenceState? presenceState;
  final Widget Function(DesktopWindowProjection window) buildWindowViewHost;
  final ValueChanged<String> onLaunchApp;
  final VoidCallback onToggleShowDesktop;
  final ValueChanged<String> onFocusWindow;
  final ValueChanged<DesktopWindowProjection> onCloseWindow;
  final ValueChanged<String> onOpenWidget;
  final ValueChanged<String> onCloseWidget;
  final VoidCallback onAutoArrange;
  final VoidCallback onTogglePalette;
  final ValueChanged<IcStylePreset> onSelectPreset;
  final VoidCallback onCycleBarLayout;
  final ValueChanged<bool> onSetAllAppsOpen;
  final ValueChanged<bool> onSetThemeStudioOpen;
  final VoidCallback onClearOverlays;
  final DesktopGuestChromeConfig guestChrome;
  final Widget? profileMenu;
  final Widget? presenceLayer;

  List<UiNavBarItem> _buildAdaptiveNavItems() => [
    const UiNavBarItem(id: 'ino', label: 'Ino', icon: LucideIcons.sparkles),
    UiNavBarItem(
      id: 'active',
      label: 'Active',
      icon: LucideIcons.layers,
      badgeCount: visibleWindows.isNotEmpty ? visibleWindows.length : null,
    ),
    const UiNavBarItem(
      id: 'apps',
      label: 'All Apps',
      icon: LucideIcons.layoutGrid,
    ),
    const UiNavBarItem(
      id: 'theme',
      label: 'Theme',
      icon: LucideIcons.slidersHorizontal,
    ),
  ];

  int _adaptiveSelectedIndex() {
    if (themeStudioOpen) return 3;
    if (allAppsOpen) return 2;
    if (topWindow != null && topWindow!.appId != 'ino') return 1;
    return 0;
  }

  Widget _adaptiveTopActions() => Positioned(
    top: 8,
    right: 8,
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        IconButton(
          tooltip: 'Home',
          isSelected: showDesktopOnly,
          icon: const Icon(LucideIcons.brainCircuit),
          onPressed: () {
            onClearOverlays();
            onToggleShowDesktop();
          },
        ),
        IconButton(
          tooltip: 'Settings',
          icon: const Icon(LucideIcons.settings),
          onPressed: () {
            onClearOverlays();
            onLaunchApp('settings');
          },
        ),
        if (profileMenu != null) SizedBox(height: 32, child: profileMenu),
      ],
    ),
  );

  Widget _buildSinglePaneContent() {
    final activeWindow = topWindow;
    if (activeWindow == null) {
      return SmartDesktop(
        hasOpenWindows: false,
        stylePreset: stylePreset,
        barLayout: barLayout,
        apps: dockApps,
        openCardIds: openWidgets,
        onOpenCard: onOpenWidget,
        onCloseCard: onCloseWidget,
        onLaunchApp: onLaunchApp,
        onOpenPalette: onTogglePalette,
        onOpenAllApps: () {
          onClearOverlays();
          onSetAllAppsOpen(true);
        },
        onSelectPreset: onSelectPreset,
        onCycleBarLayout: onCycleBarLayout,
        onAutoArrange: onAutoArrange,
      );
    }

    return buildWindowViewHost(activeWindow);
  }

  /// Presents the Active Apps bottom sheet for compact and medium layouts.
  static Future<void> showActiveAppsSheet({
    required BuildContext context,
    required List<DesktopWindowProjection> mruWindows,
    required ValueChanged<String> onFocusWindow,
    required ValueChanged<DesktopWindowProjection> onCloseWindow,
  }) {
    return showUiSheet<void>(
      context: context,
      compactBreakpoint: kDesktopCompactBreakpoint,
      builder: (sheetCtx) {
        final uiColors = UiColors.of(sheetCtx);
        return UiSheet(
          title: 'Active Apps',
          subtitle: mruWindows.isEmpty
              ? 'No open apps'
              : '${mruWindows.length} running',
          showDragHandle: true,
          semanticsId: 'adaptive-sheet:active-apps',
          child: mruWindows.isEmpty
              ? Padding(
                  padding: const EdgeInsets.symmetric(vertical: 16),
                  child: Text(
                    'Launch an app from Ino or All Apps to get started.',
                    style: TextStyle(color: uiColors.muted, fontSize: 13),
                  ),
                )
              : ConstrainedBox(
                  constraints: const BoxConstraints(maxHeight: 360),
                  child: ListView.separated(
                    shrinkWrap: true,
                    itemCount: mruWindows.length,
                    separatorBuilder: (_, _) => const SizedBox(height: 6),
                    itemBuilder: (_, idx) {
                      final w = mruWindows[idx];
                      return Material(
                        type: MaterialType.transparency,
                        child: ListTile(
                          dense: true,
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(10),
                          ),
                          tileColor: uiColors.surface,
                          leading: IcAppIcon(appId: w.appId, size: 22),
                          title: Text(
                            w.title,
                            style: TextStyle(
                              color: uiColors.strong,
                              fontWeight: FontWeight.w600,
                            ),
                          ),
                          trailing: IconButton(
                            icon: Icon(
                              LucideIcons.x,
                              size: 16,
                              color: uiColors.muted,
                            ),
                            tooltip: 'Close ${w.title}',
                            onPressed: () {
                              Navigator.of(sheetCtx).pop();
                              onCloseWindow(w);
                            },
                          ),
                          onTap: () {
                            Navigator.of(sheetCtx).pop();
                            onFocusWindow(w.id);
                          },
                        ),
                      );
                    },
                  ),
                ),
        );
      },
    );
  }

  /// Presents the All Apps launcher grid sheet for compact layouts.
  static Future<void> showAllAppsSheet({
    required BuildContext context,
    required List<SmartAppTileSpec> launcherApps,
    required ValueChanged<String> onLaunchApp,
  }) {
    return showUiSheet<void>(
      context: context,
      compactBreakpoint: kDesktopCompactBreakpoint,
      builder: (sheetCtx) {
        final uiColors = UiColors.of(sheetCtx);
        return UiSheet(
          title: 'All Apps',
          subtitle: '${launcherApps.length} available',
          showDragHandle: true,
          semanticsId: 'adaptive-sheet:all-apps',
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxHeight: 420),
            child: GridView.builder(
              shrinkWrap: true,
              gridDelegate: const SliverGridDelegateWithFixedCrossAxisCount(
                crossAxisCount: 3,
                mainAxisSpacing: 10,
                crossAxisSpacing: 10,
                childAspectRatio: 0.95,
              ),
              itemCount: launcherApps.length,
              itemBuilder: (_, idx) {
                final spec = launcherApps[idx];
                return InkWell(
                  borderRadius: BorderRadius.circular(12),
                  onTap: () {
                    Navigator.of(sheetCtx).pop();
                    onLaunchApp(spec.id);
                  },
                  child: Container(
                    padding: const EdgeInsets.all(8),
                    decoration: BoxDecoration(
                      color: uiColors.surface,
                      borderRadius: BorderRadius.circular(12),
                      border: Border.all(color: uiColors.hairline),
                    ),
                    child: Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        IcAppIcon(
                          appId: spec.id,
                          icon: spec.icon,
                          accentHex: spec.accentHex,
                          size: 26,
                        ),
                        const SizedBox(height: 6),
                        Text(
                          spec.name,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          textAlign: TextAlign.center,
                          style: TextStyle(
                            color: uiColors.strong,
                            fontSize: 11.5,
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                      ],
                    ),
                  ),
                );
              },
            ),
          ),
        );
      },
    );
  }

  /// Presents the Live Theme Studio sheet for compact layouts.
  static Future<void> showThemeStudioSheet(BuildContext context) {
    final controller = UiThemeSeedsController.maybeOf(context);
    var localSeeds = controller?.seeds ?? UiThemeSeeds.warmPaper;
    return showUiSheet<void>(
      context: context,
      compactBreakpoint: kDesktopCompactBreakpoint,
      builder: (sheetCtx) => StatefulBuilder(
        builder: (ctx, setSheetState) {
          final currentSeeds =
              UiThemeSeedsController.maybeOf(ctx)?.seeds ?? localSeeds;
          return UiSheet(
            showDragHandle: true,
            semanticsId: 'adaptive-sheet:theme-studio',
            padding: const EdgeInsets.all(12),
            child: SingleChildScrollView(
              child: ThemeStudioPanel(
                seeds: currentSeeds,
                onChanged: (next) {
                  if (controller != null) {
                    controller.setSeeds(next);
                  } else {
                    setSheetState(() => localSeeds = next);
                  }
                },
              ),
            ),
          );
        },
      ),
    );
  }

  Widget _buildCompactShell(BuildContext context, UiColors c) {
    final controller = UiThemeSeedsController.maybeOf(context);
    final compactSeeds = (controller?.seeds ?? UiThemeSeeds.warmPaper).copyWith(
      density: UiDensity.spacious,
    );

    return UiSeedThemeScope(
      key: const ValueKey('adaptive-compact-theme-scope'),
      seeds: compactSeeds,
      child: Builder(
        builder: (compactCtx) => DecoratedBox(
          key: const ValueKey('adaptive-shell-compact'),
          decoration: BoxDecoration(color: c.surfacePage),
          child: Column(
            children: [
              Expanded(
                child: Stack(
                  children: [
                    Positioned.fill(child: _buildSinglePaneContent()),
                    if (topWindow?.appId != 'ino')
                      Positioned(
                        right: 12,
                        bottom: 12,
                        child: InoOrbV2(
                          state: presenceState ?? PresenceState.idle,
                          size: 48,
                          semanticsId: 'compact:ino-orb',
                          onTap: () {
                            onClearOverlays();
                            onLaunchApp('ino');
                          },
                        ),
                      ),
                    _adaptiveTopActions(),
                    Positioned.fill(
                      child: DesktopChromeOverlayHost(
                        guestChrome: guestChrome,
                        onBeforeOpenDownloadModal: onClearOverlays,
                      ),
                    ),
                  ],
                ),
              ),
              UiAdaptiveNavBar(
                compactBreakpoint: kDesktopCompactBreakpoint,
                semanticsId: 'desktop-adaptive-nav',
                items: _buildAdaptiveNavItems(),
                selectedIndex: _adaptiveSelectedIndex(),
                onSelected: (index) {
                  switch (index) {
                    case 0:
                      onClearOverlays();
                      onLaunchApp('ino');
                    case 1:
                      unawaited(
                        showActiveAppsSheet(
                          context: compactCtx,
                          mruWindows: mruWindows,
                          onFocusWindow: onFocusWindow,
                          onCloseWindow: onCloseWindow,
                        ),
                      );
                    case 2:
                      unawaited(
                        showAllAppsSheet(
                          context: compactCtx,
                          launcherApps: launcherApps,
                          onLaunchApp: onLaunchApp,
                        ),
                      );
                    case 3:
                      unawaited(showThemeStudioSheet(compactCtx));
                  }
                },
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildMediumShell(BuildContext context, UiColors c) {
    return DecoratedBox(
      key: const ValueKey('adaptive-shell-medium'),
      decoration: BoxDecoration(color: c.surfacePage),
      child: Row(
        children: [
          UiAdaptiveNavBar(
            compactBreakpoint: kDesktopCompactBreakpoint,
            semanticsId: 'desktop-adaptive-nav',
            items: _buildAdaptiveNavItems(),
            selectedIndex: _adaptiveSelectedIndex(),
            onSelected: (index) {
              switch (index) {
                case 0:
                  onClearOverlays();
                  onLaunchApp('ino');
                case 1:
                  onClearOverlays();
                  unawaited(
                    showActiveAppsSheet(
                      context: context,
                      mruWindows: mruWindows,
                      onFocusWindow: onFocusWindow,
                      onCloseWindow: onCloseWindow,
                    ),
                  );
                case 2:
                  onSetThemeStudioOpen(false);
                  onSetAllAppsOpen(!allAppsOpen);
                case 3:
                  onSetAllAppsOpen(false);
                  onSetThemeStudioOpen(!themeStudioOpen);
              }
            },
          ),
          Expanded(
            child: Stack(
              children: [
                Positioned.fill(child: _buildSinglePaneContent()),
                _adaptiveTopActions(),
                Positioned.fill(
                  child: DesktopChromeOverlayHost(
                    presenceLayer: presenceLayer,
                    allAppsOpen: allAppsOpen,
                    launcherApps: launcherApps,
                    onCloseAllApps: () => onSetAllAppsOpen(false),
                    onLaunchApp: (appId) {
                      onSetAllAppsOpen(false);
                      onLaunchApp(appId);
                    },
                    themeStudioOpen: themeStudioOpen,
                    onCloseThemeStudio: () => onSetThemeStudioOpen(false),
                    themeStudioTop: 16,
                    guestChrome: guestChrome,
                    onBeforeOpenDownloadModal: onClearOverlays,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    return switch (mode) {
      DesktopAdaptiveLayoutMode.compact => _buildCompactShell(context, c),
      DesktopAdaptiveLayoutMode.medium => _buildMediumShell(context, c),
    };
  }
}
