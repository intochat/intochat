import 'dart:math' as math;
import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'guest_status_pill.dart';
import 'os_bar.dart';
import 'smart_desktop.dart';

part 'desktop_bar_popovers.dart';

/// Canonical Day-0 guest/ephemeral session check shared by `WorkspaceApp`
/// and `LiveDesktopShell`.
///
/// Explicit [isGuest] or [isEphemeral] overrides take precedence when non-null;
/// otherwise falls back to [clientIsGuest] or a `try-` brain prefix.
bool resolveIsGuestSession({
  bool? isGuest,
  bool? isEphemeral,
  bool clientIsGuest = false,
  String? brainId,
}) {
  if (isGuest != null) return isGuest;
  if (isEphemeral != null) return isEphemeral;
  return clientIsGuest || (brainId?.startsWith('try-') ?? false);
}

enum DesktopPopoverKind { none, clock, tokens, notifications, dockMenu }

/// Configuration for the shared Day-0 guest chrome layer (`GuestStatusPill`
/// and `DownloadAppModal`) hosted by [DesktopChromeOverlayHost].
@immutable
class DesktopGuestChromeConfig {
  const DesktopGuestChromeConfig({
    required this.showPill,
    this.remaining,
    this.onSaveBrain,
    this.onDownloadApp,
    this.onOpenUrl,
    this.downloadHubUri,
    this.useSafeArea = false,
  });

  final bool showPill;
  final Duration? remaining;
  final VoidCallback? onSaveBrain;
  final VoidCallback? onDownloadApp;
  final void Function(Uri url)? onOpenUrl;
  final Uri? downloadHubUri;
  final bool useSafeArea;
}

/// Configuration for the expanded OS Bar popover layer hosted by
/// [DesktopChromeOverlayHost].
@immutable
class DesktopPopoverOverlayConfig {
  const DesktopPopoverOverlayConfig({
    required this.kind,
    required this.screen,
    required this.barLayout,
    required this.anchorX,
    required this.notificationsCount,
    required this.notifications,
    required this.notificationsLoaded,
    required this.notificationsLoading,
    required this.pinnedAppIds,
    required this.windows,
    required this.onClose,
    required this.onOpenSettings,
    required this.onOpenComputeSettings,
    required this.onMarkNotificationRead,
    required this.onMarkAllNotificationsRead,
    required this.onClearNotifications,
    required this.onFocusWindow,
    required this.onNewWindowForApp,
    required this.onTogglePinApp,
    required this.onQuitApp,
    this.dockAppId,
    this.dockAppTitle,
    this.backdropBottom = kOsBarHeight,
  });

  final DesktopPopoverKind kind;
  final Size screen;
  final BarLayout barLayout;
  final double anchorX;
  final String? dockAppId;
  final String? dockAppTitle;
  final int notificationsCount;
  final List<OsNotificationItem> notifications;
  final bool notificationsLoaded;
  final bool notificationsLoading;
  final Set<String> pinnedAppIds;
  final List<DesktopWindowSummary> windows;
  final VoidCallback onClose;
  final VoidCallback onOpenSettings;
  final VoidCallback onOpenComputeSettings;
  final ValueChanged<String> onMarkNotificationRead;
  final VoidCallback onMarkAllNotificationsRead;
  final VoidCallback onClearNotifications;
  final ValueChanged<String> onFocusWindow;
  final ValueChanged<String> onNewWindowForApp;
  final ValueChanged<String> onTogglePinApp;
  final ValueChanged<String> onQuitApp;
  final double backdropBottom;
}

/// Configuration for the `Alt+\`` MRU window switcher overlay hosted by
/// [DesktopChromeOverlayHost].
@immutable
class DesktopWindowSwitcherConfig {
  const DesktopWindowSwitcherConfig({
    required this.windows,
    required this.selectedIndex,
    required this.onSelectWindow,
    required this.onCancel,
  });

  final List<DesktopWindowSummary> windows;
  final int selectedIndex;
  final ValueChanged<DesktopWindowSummary> onSelectWindow;
  final VoidCallback onCancel;
}

/// Canonical overlay host shared by `LiveDesktopShell`, `DesktopAdaptiveShell`,
/// and `WorkspaceApp`.
///
/// Unifies popover backdrop + [DesktopBarPopover], [AltBacktickWindowSwitcher],
/// optional presence layer, [AllAppsLauncherOverlay], [ThemeStudioPanel], and
/// [GuestChromeOverlay] (including owned download modal open/close & Escape
/// handling) in one place.
class DesktopChromeOverlayHost extends StatefulWidget {
  const DesktopChromeOverlayHost({
    super.key,
    this.presenceLayer,
    this.popover,
    this.windowSwitcher,
    this.allAppsOpen = false,
    this.launcherApps = const [],
    this.onCloseAllApps,
    this.onLaunchApp,
    this.themeStudioOpen = false,
    this.onCloseThemeStudio,
    this.themeStudioTop,
    this.themeStudioBottom,
    this.themeStudioBackdropBottom = 0,
    this.guestChrome,
    this.onBeforeOpenDownloadModal,
  });

  final Widget? presenceLayer;
  final DesktopPopoverOverlayConfig? popover;
  final DesktopWindowSwitcherConfig? windowSwitcher;
  final bool allAppsOpen;
  final List<SmartAppTileSpec> launcherApps;
  final VoidCallback? onCloseAllApps;
  final ValueChanged<String>? onLaunchApp;
  final bool themeStudioOpen;
  final VoidCallback? onCloseThemeStudio;
  final double? themeStudioTop;
  final double? themeStudioBottom;
  final double themeStudioBackdropBottom;
  final DesktopGuestChromeConfig? guestChrome;
  final VoidCallback? onBeforeOpenDownloadModal;

  @override
  State<DesktopChromeOverlayHost> createState() =>
      _DesktopChromeOverlayHostState();
}

class _DesktopChromeOverlayHostState extends State<DesktopChromeOverlayHost> {
  bool _downloadModalOpen = false;

  @override
  void initState() {
    super.initState();
    HardwareKeyboard.instance.addHandler(_onHardwareKey);
  }

  @override
  void didUpdateWidget(DesktopChromeOverlayHost oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.guestChrome == null && _downloadModalOpen) {
      _downloadModalOpen = false;
    }
  }

  @override
  void dispose() {
    HardwareKeyboard.instance.removeHandler(_onHardwareKey);
    super.dispose();
  }

  bool _onHardwareKey(KeyEvent event) {
    if (!mounted || !_downloadModalOpen) return false;
    if (event is KeyDownEvent &&
        event.logicalKey == LogicalKeyboardKey.escape) {
      setState(() => _downloadModalOpen = false);
      return true;
    }
    return false;
  }

  void _handleDownloadAppTap() {
    final override = widget.guestChrome?.onDownloadApp;
    if (override != null) {
      override();
      return;
    }
    widget.onBeforeOpenDownloadModal?.call();
    setState(() => _downloadModalOpen = true);
  }

  @override
  Widget build(BuildContext context) {
    final popover = widget.popover;
    final hasPopover =
        popover != null && popover.kind != DesktopPopoverKind.none;
    final switcher = widget.windowSwitcher;
    final hasSwitcher = switcher != null && switcher.windows.isNotEmpty;
    final hasPresence = widget.presenceLayer != null;
    final hasAllApps = widget.allAppsOpen;
    final hasThemeStudio = widget.themeStudioOpen;
    final guest = widget.guestChrome;
    final hasGuest = guest != null && (guest.showPill || _downloadModalOpen);

    if (!hasPresence &&
        !hasPopover &&
        !hasSwitcher &&
        !hasAllApps &&
        !hasThemeStudio &&
        !hasGuest) {
      return const SizedBox.shrink();
    }

    return Stack(
      clipBehavior: Clip.none,
      children: [
        if (hasPresence) Positioned.fill(child: widget.presenceLayer!),
        if (hasPopover) ...[
          Positioned(
            left: 0,
            right: 0,
            top: 0,
            bottom: popover.backdropBottom,
            child: GestureDetector(
              behavior: HitTestBehavior.opaque,
              onTap: popover.onClose,
            ),
          ),
          DesktopBarPopover(
            kind: popover.kind,
            screen: popover.screen,
            barLayout: popover.barLayout,
            anchorX: popover.anchorX,
            dockAppId: popover.dockAppId,
            dockAppTitle: popover.dockAppTitle,
            notificationsCount: popover.notificationsCount,
            notifications: popover.notifications,
            notificationsLoaded: popover.notificationsLoaded,
            notificationsLoading: popover.notificationsLoading,
            pinnedAppIds: popover.pinnedAppIds,
            windows: popover.windows,
            onClose: popover.onClose,
            onOpenSettings: popover.onOpenSettings,
            onOpenComputeSettings: popover.onOpenComputeSettings,
            onMarkNotificationRead: popover.onMarkNotificationRead,
            onMarkAllNotificationsRead: popover.onMarkAllNotificationsRead,
            onClearNotifications: popover.onClearNotifications,
            onFocusWindow: popover.onFocusWindow,
            onNewWindowForApp: popover.onNewWindowForApp,
            onTogglePinApp: popover.onTogglePinApp,
            onQuitApp: popover.onQuitApp,
          ),
        ],
        if (hasSwitcher)
          Positioned.fill(
            child: AltBacktickWindowSwitcher(
              windows: switcher.windows,
              selectedIndex: switcher.selectedIndex.clamp(
                0,
                switcher.windows.length - 1,
              ),
              onSelectWindow: switcher.onSelectWindow,
              onCancel: switcher.onCancel,
            ),
          ),
        if (hasAllApps)
          Positioned.fill(
            child: AllAppsLauncherOverlay(
              apps: widget.launcherApps,
              onClose: widget.onCloseAllApps ?? () {},
              onLaunchApp: widget.onLaunchApp ?? (_) {},
            ),
          ),
        if (hasThemeStudio) ...[
          Positioned(
            left: 0,
            right: 0,
            top: 0,
            bottom: widget.themeStudioBackdropBottom,
            child: GestureDetector(
              behavior: HitTestBehavior.opaque,
              onTap: widget.onCloseThemeStudio,
            ),
          ),
          Positioned(
            right: 16,
            top: widget.themeStudioTop,
            bottom: widget.themeStudioBottom,
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 340),
              child: Builder(
                builder: (ctx) {
                  final controller = UiThemeSeedsController.maybeOf(ctx);
                  return ThemeStudioPanel(
                    seeds: controller?.seeds ?? UiThemeSeeds.warmPaper,
                    onChanged: controller?.setSeeds ?? (_) {},
                  );
                },
              ),
            ),
          ),
        ],
        if (hasGuest)
          Positioned.fill(
            child: GuestChromeOverlay(
              showPill: guest.showPill,
              downloadModalOpen: _downloadModalOpen,
              remaining: guest.remaining,
              onSaveBrain: guest.onSaveBrain,
              onDownloadApp: _handleDownloadAppTap,
              onCloseDownloadModal: () =>
                  setState(() => _downloadModalOpen = false),
              onOpenUrl: guest.onOpenUrl,
              downloadHubUri: guest.downloadHubUri,
              useSafeArea: guest.useSafeArea,
            ),
          ),
      ],
    );
  }
}

@immutable
class DesktopWindowSummary {
  const DesktopWindowSummary({
    required this.windowId,
    required this.numericId,
    required this.appId,
    required this.title,
    this.minimized = false,
    this.isPinned = true,
  });

  final String windowId;
  final int numericId;
  final String appId;
  final String title;
  final bool minimized;
  final bool isPinned;
}

/// `Alt+\`` (`⌥\``) visual MRU window thumbnail switcher with `IcAppIcon` previews.
class AltBacktickWindowSwitcher extends StatelessWidget {
  const AltBacktickWindowSwitcher({
    super.key,
    required this.windows,
    required this.selectedIndex,
    required this.onSelectWindow,
    required this.onCancel,
  });

  final List<DesktopWindowSummary> windows;
  final int selectedIndex;
  final ValueChanged<DesktopWindowSummary> onSelectWindow;
  final VoidCallback onCancel;

  @override
  Widget build(BuildContext context) {
    if (windows.isEmpty) return const SizedBox.shrink();
    final c = UiColors.of(context);

    return GestureDetector(
      key: const ValueKey('desktop-window-switcher'),
      behavior: HitTestBehavior.opaque,
      onTap: onCancel,
      child: ColoredBox(
        color: Color.fromRGBO(0, 0, 0, c.isDark ? 0.42 : 0.22),
        child: Center(
          child: ClipRRect(
            borderRadius: BorderRadius.circular(UiTokens.of(context).window),
            child: BackdropFilter(
              filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
              child: Container(
                padding: const EdgeInsets.all(14),
                decoration: BoxDecoration(
                  color: c.surfaceElevated.withValues(alpha: 0.94),
                  borderRadius: BorderRadius.circular(
                    UiTokens.of(context).window,
                  ),
                  border: Border.all(
                    color: c.isGlass ? c.specularBorder : c.borderHairline,
                  ),
                  boxShadow: UiShadows.xl(c),
                ),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        for (var i = 0; i < windows.length; i++) ...[
                          if (i > 0) const SizedBox(width: 10),
                          _WindowThumb(
                            window: windows[i],
                            selected: i == selectedIndex,
                            onTap: () => onSelectWindow(windows[i]),
                          ),
                        ],
                      ],
                    ),
                    const SizedBox(height: 10),
                    Text(
                      '⌥`   Release ⌥ to switch  ·  Esc to cancel',
                      style: UiType.textStyle(12, color: c.textMuted),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _WindowThumb extends StatefulWidget {
  const _WindowThumb({
    required this.window,
    required this.selected,
    required this.onTap,
  });

  final DesktopWindowSummary window;
  final bool selected;
  final VoidCallback onTap;

  @override
  State<_WindowThumb> createState() => _WindowThumbState();
}

class _WindowThumbState extends State<_WindowThumb> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final sel = widget.selected;
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Container(
          width: 168,
          padding: const EdgeInsets.all(10),
          decoration: BoxDecoration(
            color: sel
                ? c.accentTint
                : (_hover ? c.surfaceSunk : c.surfaceCard),
            borderRadius: BorderRadius.circular(UiTokens.of(context).card),
            border: Border.all(
              color: sel ? c.accent : c.borderHairline,
              width: sel ? 2 : 1,
            ),
          ),
          child: Column(
            children: [
              Container(
                height: 84,
                decoration: BoxDecoration(
                  color: c.surfacePage,
                  borderRadius: BorderRadius.circular(UiTokens.of(context).sm),
                  border: Border.all(color: c.borderHairline),
                ),
                child: Column(
                  children: [
                    Container(
                      height: 16,
                      padding: const EdgeInsets.symmetric(horizontal: 6),
                      decoration: BoxDecoration(
                        color: c.titleBarFocused,
                        borderRadius: BorderRadius.vertical(
                          top: Radius.circular(UiTokens.of(context).sm - 1),
                        ),
                      ),
                      child: Row(
                        children: [
                          IcAppIcon(appId: widget.window.appId, size: 10),
                          const SizedBox(width: 4),
                          Expanded(
                            child: Text(
                              widget.window.title,
                              overflow: TextOverflow.ellipsis,
                              style: UiType.textStyle(9.5, color: c.textMuted),
                            ),
                          ),
                        ],
                      ),
                    ),
                    Expanded(
                      child: Center(
                        child: IcAppIcon(appId: widget.window.appId, size: 30),
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 8),
              Text(
                widget.window.title,
                overflow: TextOverflow.ellipsis,
                style: UiType.textStyle(
                  12.5,
                  weight: FontWeight.w600,
                  color: c.textStrong,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
