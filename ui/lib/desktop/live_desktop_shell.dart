import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import '../src/widgets/ino/ino_orb_v2.dart';
import '../workspace/shell_activation_router.dart';
import '../workspace/spatial_app_canvas.dart';
import 'desktop_overlays.dart';
import 'desktop_window_controller.dart';
import 'guest_status_pill.dart';
import 'os_bar.dart';
import 'presence_layer.dart';
import 'smart_desktop.dart';
import 'window_frame.dart';

/// Live Desktop OS Shell & Floating Acrylic Window Manager backed by
/// [DesktopShellProjection], [BrainSessionScope], [DesktopWindowController],
/// and [ShellActivationRouter].
class LiveDesktopShell extends StatefulWidget {
  const LiveDesktopShell({
    super.key,
    required this.stylePreset,
    required this.barLayout,
    required this.paletteOpen,
    required this.onTogglePalette,
    required this.onSelectPreset,
    required this.onCycleBarLayout,
    this.onOpenCompute,
    this.profileMenu,
    this.isGuest = false,
    this.isEphemeral = false,
    this.guestRemaining,
    this.onSaveBrain,
    this.onDownloadApp,
    this.onOpenUrl,
    this.downloadHubUri,
    this.activationRouter,
    this.windowController,
  });

  final IcStylePreset stylePreset;
  final BarLayout barLayout;
  final bool paletteOpen;
  final VoidCallback onTogglePalette;
  final ValueChanged<IcStylePreset> onSelectPreset;
  final VoidCallback onCycleBarLayout;
  final VoidCallback? onOpenCompute;
  final Widget? profileMenu;

  /// True when the active session runs on a Day-0 guest brain.
  final bool isGuest;

  /// True when the active brain is ephemeral (in-RAM temporary session).
  final bool isEphemeral;

  /// Optional remaining lifetime shown in the guest status pill (defaults to `24h`).
  final Duration? guestRemaining;

  /// Triggered when the guest taps `Save My Brain →`.
  final VoidCallback? onSaveBrain;

  /// Optional override when the guest taps `Download App ↓`; when omitted, opens [DownloadAppModal].
  final VoidCallback? onDownloadApp;

  /// Opens an external download asset or `/download` hub URL.
  final void Function(Uri url)? onOpenUrl;

  /// Optional deep-link URI for the host's `/download` hub.
  final Uri? downloadHubUri;

  /// Shared activation/navigation router owned by `WorkspaceApp`. When omitted
  /// (e.g. in standalone widget tests), a local router backed by
  /// [BrainSessionScope] is created automatically.
  final ShellActivationRouter? activationRouter;

  /// Optional externally managed [DesktopWindowController].
  final DesktopWindowController? windowController;

  @override
  State<LiveDesktopShell> createState() => _LiveDesktopShellState();
}

class _LiveDesktopShellState extends State<LiveDesktopShell> {
  static const double _kCompactBreakpoint = 600.0;
  static const double _kExpandedBreakpoint = 1024.0;

  DesktopWindowController? _windowController;
  bool _ownsWindowController = false;
  ShellActivationRouter? _localRouter;
  ShellActivationRouter? _attachedRouter;
  DesktopActivationDelegate? _desktopDelegate;
  BrainSessionController? _attachedSession;

  DesktopPopoverKind _popover = DesktopPopoverKind.none;
  double _popoverAnchorX = 0;
  String? _dockMenuAppId;
  String? _barTooltipLabel;
  double _barTooltipAnchorX = 0;
  bool _allAppsOpen = false;
  bool _themeStudioOpen = false;
  bool _downloadModalOpen = false;
  bool _switcherOpen = false;
  int _switcherIndex = 0;
  Size? _pendingDeskSize;

  BrainSessionController get _session => BrainSessionScope.readOf(context)!;
  DesktopWindowController get _windows => _windowController!;
  ShellActivationRouter get _router => widget.activationRouter ?? _localRouter!;

  bool _isGuestSession(String brainId) =>
      widget.isGuest || widget.isEphemeral || brainId.startsWith('try-');

  @override
  void initState() {
    super.initState();
    HardwareKeyboard.instance.addHandler(_onHardwareKey);
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final session = BrainSessionScope.of(context);
    if (!identical(_attachedSession, session)) {
      _attachedSession?.enableDesktopSession(false, notify: false);
      _attachedSession = session;
    }
    session.enableDesktopSession(true);

    if (widget.windowController != null) {
      if (!identical(_windowController, widget.windowController)) {
        _detachWindowController();
        _windowController = widget.windowController;
        _ownsWindowController = false;
        _windowController!.addListener(_onWindowControllerChanged);
      }
      _windowController!.attachSession(session);
    } else if (_windowController == null) {
      _windowController = DesktopWindowController(session: session);
      _ownsWindowController = true;
      _windowController!.addListener(_onWindowControllerChanged);
    } else {
      _windowController!.attachSession(session);
    }

    _syncRouterAttachment(session);
  }

  @override
  void didUpdateWidget(LiveDesktopShell oldWidget) {
    super.didUpdateWidget(oldWidget);
    final session = _attachedSession;
    if (session == null) return;
    if (oldWidget.windowController != widget.windowController) {
      _detachWindowController();
      if (widget.windowController != null) {
        _windowController = widget.windowController;
        _ownsWindowController = false;
      } else {
        _windowController = DesktopWindowController(session: session);
        _ownsWindowController = true;
      }
      _windowController!.addListener(_onWindowControllerChanged);
      _windowController!.attachSession(session);
    }
    if (oldWidget.activationRouter != widget.activationRouter) {
      _syncRouterAttachment(session);
    }
  }

  void _syncRouterAttachment(BrainSessionController session) {
    final targetRouter =
        widget.activationRouter ??
        (_localRouter ??= ShellActivationRouter(
          readState: () => ShellRoutingState(
            currentBrain: _attachedSession?.brainId ?? session.brainId,
            isDesktopRoot: true,
          ),
          sendAct: (_, event) =>
              (_attachedSession ?? session).sendAction(event),
          isMounted: () => mounted,
        ));

    final isStandalone = widget.activationRouter == null;
    final nextDelegate = DesktopActivationDelegate(
      findOpenWindow: (appId) => _windows.findOpenWindowForApp(appId),
      focusWindow: (win) => _windows.focusWindow(win),
      openApp: (appId) => (_attachedSession ?? session).openApp(appId),
      onShowDesktopCleared: () => _windows.setShowDesktopOnly(false),
      onWindowNavigated: ({windowId, appId}) =>
          _windows.bumpReloadEpoch(windowId: windowId, appId: appId),
      requestShellOperation: (app, op, input) =>
          (_attachedSession ?? session).requestShellOperation(app, op, input),
      onNotifyStandaloneActivation: isStandalone
          ? (action) => (_attachedSession ?? session).activate(action)
          : null,
    );

    if (_attachedRouter != null && _desktopDelegate != null) {
      _attachedRouter!.detachDesktop(_desktopDelegate!);
    }
    _desktopDelegate = nextDelegate;
    _attachedRouter = targetRouter;
    targetRouter.attachDesktop(nextDelegate);
  }

  void _onWindowControllerChanged() {
    if (mounted) setState(() {});
  }

  void _detachWindowController() {
    final wc = _windowController;
    if (wc == null) return;
    wc.removeListener(_onWindowControllerChanged);
    if (_ownsWindowController) {
      wc.dispose();
    }
    _windowController = null;
    _ownsWindowController = false;
  }

  @override
  void dispose() {
    HardwareKeyboard.instance.removeHandler(_onHardwareKey);
    if (_attachedRouter != null && _desktopDelegate != null) {
      _attachedRouter!.detachDesktop(_desktopDelegate!);
    }
    _attachedRouter = null;
    _desktopDelegate = null;
    _attachedSession?.enableDesktopSession(false, notify: false);
    _attachedSession = null;
    _detachWindowController();
    super.dispose();
  }

  void _scheduleDeskSizeSync(Size desk) {
    if (_windows.deskSize == desk || _pendingDeskSize == desk) return;
    _pendingDeskSize = desk;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      final next = _pendingDeskSize;
      _pendingDeskSize = null;
      if (!mounted || next == null) return;
      _windows.updateDeskSize(next);
    });
  }

  void _handleDownloadAppTap() {
    if (widget.onDownloadApp != null) {
      widget.onDownloadApp!();
      return;
    }
    setState(() {
      _popover = DesktopPopoverKind.none;
      _allAppsOpen = false;
      _themeStudioOpen = false;
      _downloadModalOpen = true;
    });
  }

  void _launchOrFocusApp(
    String appId, {
    String? settingsSection,
    String? openInput,
  }) {
    unawaited(
      _router.launchOrFocusApp(
        appId,
        settingsSection: settingsSection,
        openInput: openInput,
      ),
    );
  }

  void _openAppGraph(String appId) => _launchOrFocusApp(
    'creator',
    openInput: 'node:${_session.brainId}/apps/$appId',
  );

  void _inspectSandbox(String appId) {
    unawaited(
      _router.launchOrFocusApp(
        'creator',
        operation: 'inspect',
        extra: {'resource': 'app.$appId'},
      ),
    );
  }

  bool _onHardwareKey(KeyEvent e) {
    if (!mounted) return false;
    final kb = HardwareKeyboard.instance;

    if (e is KeyUpEvent &&
        (e.physicalKey == PhysicalKeyboardKey.altLeft ||
            e.physicalKey == PhysicalKeyboardKey.altRight ||
            e.logicalKey == LogicalKeyboardKey.altLeft ||
            e.logicalKey == LogicalKeyboardKey.altRight)) {
      if (_switcherOpen) {
        final mru = _windows.mruWindows;
        setState(() => _switcherOpen = false);
        if (mru.isNotEmpty) {
          _windows.focusWindow(mru[_switcherIndex.clamp(0, mru.length - 1)]);
        }
        return true;
      }
      return false;
    }

    if (e is KeyUpEvent) return false;

    if (kb.isAltPressed &&
        (e.physicalKey == PhysicalKeyboardKey.backquote ||
            e.logicalKey == LogicalKeyboardKey.backquote)) {
      final mru = _windows.mruWindows;
      if (mru.isEmpty) return false;
      setState(() {
        _popover = DesktopPopoverKind.none;
        if (!_switcherOpen) {
          _switcherOpen = true;
          _switcherIndex = mru.length > 1 ? 1 : 0;
        } else {
          final delta = kb.isShiftPressed ? -1 : 1;
          _switcherIndex = (_switcherIndex + delta) % mru.length;
          if (_switcherIndex < 0) _switcherIndex += mru.length;
        }
      });
      return true;
    }

    if (e.logicalKey == LogicalKeyboardKey.escape && e is KeyDownEvent) {
      if (_downloadModalOpen) {
        setState(() => _downloadModalOpen = false);
        return true;
      }
      if (_themeStudioOpen) {
        setState(() => _themeStudioOpen = false);
        return true;
      }
      if (_allAppsOpen) {
        setState(() => _allAppsOpen = false);
        return true;
      }
      if (_switcherOpen) {
        setState(() => _switcherOpen = false);
        return true;
      }
      if (_popover != DesktopPopoverKind.none) {
        setState(() => _popover = DesktopPopoverKind.none);
        return true;
      }
    }

    return false;
  }

  void _togglePopover(
    DesktopPopoverKind kind, {
    double anchorX = 0,
    String? appId,
  }) {
    setState(() {
      if (_popover == kind &&
          (_dockMenuAppId == appId || kind != DesktopPopoverKind.dockMenu)) {
        _popover = DesktopPopoverKind.none;
        _dockMenuAppId = null;
      } else {
        _popover = kind;
        _popoverAnchorX = anchorX;
        _dockMenuAppId = appId;
      }
    });
    if (_popover == DesktopPopoverKind.notifications) {
      unawaited(_session.loadNotifications());
    }
  }

  List<DesktopTabSpec> _buildWindowTabs(DesktopWindowProjection spec) => [
    for (var i = 0; i < spec.tabs.length; i++)
      DesktopTabSpec(
        key: spec.tabs[i].id,
        label: spec.tabs[i].title,
        selected: spec.tabs[i].selected,
        onTap: () {
          if (!spec.tabs[i].selected) {
            unawaited(_session.selectTab(spec.id, i));
          }
        },
      ),
  ];

  Widget _buildWindowViewHost(
    DesktopWindowProjection spec, {
    required String keyPrefix,
  }) => ViewHost(
    key: ValueKey(
      '$keyPrefix:${spec.viewportUri}:${_windows.stateFor(spec.id)?.reloadEpoch ?? 0}',
    ),
    kind: 'viewport',
    name: spec.viewportUri,
    signals: _session.journalFor(spec.viewportUri),
    onActivate: (activation) =>
        unawaited(_router.activate(activation, sourceAppId: spec.appId)),
  );

  List<UiNavBarItem> _buildAdaptiveNavItems(int activeCount) => [
    const UiNavBarItem(id: 'ino', label: 'Ino', icon: LucideIcons.sparkles),
    UiNavBarItem(
      id: 'active',
      label: 'Active',
      icon: LucideIcons.layers,
      badgeCount: activeCount > 0 ? activeCount : null,
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

  Widget _adaptiveTopActions() => Positioned(
    top: 8,
    right: 8,
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        IconButton(
          tooltip: 'Home',
          isSelected: _windows.showDesktopOnly,
          icon: const Icon(LucideIcons.brainCircuit),
          onPressed: () {
            setState(() {
              _popover = DesktopPopoverKind.none;
              _allAppsOpen = false;
              _themeStudioOpen = false;
            });
            _windows.toggleShowDesktop();
          },
        ),
        IconButton(
          tooltip: 'Settings',
          icon: const Icon(LucideIcons.settings),
          onPressed: () {
            setState(() => _popover = DesktopPopoverKind.none);
            _launchOrFocusApp('settings');
          },
        ),
        if (widget.profileMenu != null)
          SizedBox(height: 32, child: widget.profileMenu),
      ],
    ),
  );

  int _adaptiveSelectedIndex(DesktopWindowProjection? topWindow) {
    if (_themeStudioOpen) return 3;
    if (_allAppsOpen) return 2;
    if (topWindow != null && topWindow.appId != 'ino') return 1;
    return 0;
  }

  Widget _buildSinglePaneContent({
    required DesktopWindowProjection? topWindow,
    required List<SmartAppTileSpec> dockApps,
    required List<String> openWidgets,
    required Size desk,
  }) {
    if (topWindow == null) {
      return SmartDesktop(
        hasOpenWindows: false,
        stylePreset: widget.stylePreset,
        barLayout: widget.barLayout,
        apps: dockApps,
        openCardIds: openWidgets,
        onOpenCard: (cardId) => unawaited(_session.openWidget(cardId)),
        onCloseCard: (cardId) => unawaited(_session.closeWidget(cardId)),
        onLaunchApp: _launchOrFocusApp,
        onOpenPalette: widget.onTogglePalette,
        onOpenAllApps: () => setState(() {
          _popover = DesktopPopoverKind.none;
          _allAppsOpen = true;
        }),
        onSelectPreset: widget.onSelectPreset,
        onCycleBarLayout: widget.onCycleBarLayout,
        onAutoArrange: () => _windows.autoArrange(desk),
      );
    }

    return _buildWindowViewHost(topWindow, keyPrefix: 'adaptive-viewport');
  }

  /// Shared overlays for PresenceLayer, AllAppsLauncherOverlay,
  /// ThemeStudioPanel, and GuestChromeOverlay reused across Compact, Medium,
  /// and Expanded shells without altering the Co-Presence Voice HUD.
  List<Widget> _buildSharedOverlays({
    required BrainSessionController session,
    required List<SmartAppTileSpec> launcherApps,
    required bool includePresence,
    required bool includeModalOverlays,
    Size? presenceDesk,
    double? themeStudioTop,
    double? themeStudioBottom,
    double themeStudioBackdropBottom = 0,
  }) => [
    if (includePresence && session.smart)
      if (presenceDesk != null)
        Positioned(
          left: 0,
          top: 0,
          width: presenceDesk.width,
          height: presenceDesk.height,
          child: _buildPresenceLayer(session),
        )
      else
        Positioned.fill(child: _buildPresenceLayer(session)),
    if (includeModalOverlays && _allAppsOpen)
      Positioned.fill(
        child: AllAppsLauncherOverlay(
          apps: launcherApps,
          onClose: () => setState(() => _allAppsOpen = false),
          onLaunchApp: (appId) {
            setState(() => _allAppsOpen = false);
            _launchOrFocusApp(appId);
          },
        ),
      ),
    if (includeModalOverlays && _themeStudioOpen) ...[
      Positioned(
        left: 0,
        right: 0,
        top: 0,
        bottom: themeStudioBackdropBottom,
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: () => setState(() => _themeStudioOpen = false),
        ),
      ),
      Positioned(
        right: 16,
        top: themeStudioTop,
        bottom: themeStudioBottom,
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
    if (_isGuestSession(session.brainId) || _downloadModalOpen)
      Positioned.fill(
        child: GuestChromeOverlay(
          showPill: _isGuestSession(session.brainId),
          downloadModalOpen: _downloadModalOpen,
          remaining: widget.guestRemaining,
          onSaveBrain: widget.onSaveBrain,
          onDownloadApp: _handleDownloadAppTap,
          onCloseDownloadModal: () =>
              setState(() => _downloadModalOpen = false),
          onOpenUrl: widget.onOpenUrl,
          downloadHubUri: widget.downloadHubUri,
        ),
      ),
  ];

  Widget _buildPresenceLayer(BrainSessionController session) => PresenceLayer(
    presence: session.presenceStream,
    enabled: session.smart,
    onOpenIno: () => _launchOrFocusApp('ino'),
    onAction: (action) {
      if (action.kind == 'open' || action.kind == 'navigate') {
        _launchOrFocusApp(action.argument);
      }
    },
  );

  Future<void> _showActiveAppsSheet(
    BuildContext context,
    List<DesktopWindowProjection> mruWindows,
  ) {
    return showUiSheet<void>(
      context: context,
      compactBreakpoint: _kCompactBreakpoint,
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
                      return ListTile(
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
                            _windows.closeWindow(w);
                          },
                        ),
                        onTap: () {
                          Navigator.of(sheetCtx).pop();
                          _windows.focusWindowById(w.id);
                        },
                      );
                    },
                  ),
                ),
        );
      },
    );
  }

  Future<void> _showAllAppsSheet(
    BuildContext context,
    List<SmartAppTileSpec> launcherApps,
  ) {
    return showUiSheet<void>(
      context: context,
      compactBreakpoint: _kCompactBreakpoint,
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
                    _launchOrFocusApp(spec.id);
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

  Future<void> _showThemeStudioSheet(BuildContext context) {
    final controller = UiThemeSeedsController.maybeOf(context);
    var localSeeds = controller?.seeds ?? UiThemeSeeds.warmPaper;
    return showUiSheet<void>(
      context: context,
      compactBreakpoint: _kCompactBreakpoint,
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

  Widget _buildCompactShell({
    required BuildContext context,
    required BrainSessionController session,
    required UiColors c,
    required Size desk,
    required List<DesktopWindowProjection> mruWindows,
    required List<DesktopWindowProjection> visibleWindows,
    required DesktopWindowProjection? topWindow,
    required List<SmartAppTileSpec> dockApps,
    required List<SmartAppTileSpec> launcherApps,
    required List<String> openWidgets,
  }) {
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
                    Positioned.fill(
                      child: _buildSinglePaneContent(
                        topWindow: topWindow,
                        dockApps: dockApps,
                        openWidgets: openWidgets,
                        desk: desk,
                      ),
                    ),
                    if (topWindow?.appId != 'ino')
                      Positioned(
                        right: 12,
                        bottom: 12,
                        child: InoOrbV2(
                          state: session.presenceState ?? PresenceState.idle,
                          size: 48,
                          semanticsId: 'compact:ino-orb',
                          onTap: () {
                            setState(() {
                              _allAppsOpen = false;
                              _themeStudioOpen = false;
                            });
                            _launchOrFocusApp('ino');
                          },
                        ),
                      ),
                    _adaptiveTopActions(),
                    ..._buildSharedOverlays(
                      session: session,
                      launcherApps: launcherApps,
                      includePresence: false,
                      includeModalOverlays: false,
                    ),
                  ],
                ),
              ),
              UiAdaptiveNavBar(
                compactBreakpoint: _kCompactBreakpoint,
                semanticsId: 'desktop-adaptive-nav',
                items: _buildAdaptiveNavItems(visibleWindows.length),
                selectedIndex: _adaptiveSelectedIndex(topWindow),
                onSelected: (index) {
                  switch (index) {
                    case 0:
                      setState(() {
                        _allAppsOpen = false;
                        _themeStudioOpen = false;
                      });
                      _launchOrFocusApp('ino');
                    case 1:
                      unawaited(_showActiveAppsSheet(compactCtx, mruWindows));
                    case 2:
                      unawaited(_showAllAppsSheet(compactCtx, launcherApps));
                    case 3:
                      unawaited(_showThemeStudioSheet(compactCtx));
                  }
                },
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildMediumShell({
    required BuildContext context,
    required BrainSessionController session,
    required UiColors c,
    required Size desk,
    required List<DesktopWindowProjection> mruWindows,
    required List<DesktopWindowProjection> visibleWindows,
    required DesktopWindowProjection? topWindow,
    required List<SmartAppTileSpec> dockApps,
    required List<SmartAppTileSpec> launcherApps,
    required List<String> openWidgets,
  }) {
    return DecoratedBox(
      key: const ValueKey('adaptive-shell-medium'),
      decoration: BoxDecoration(color: c.surfacePage),
      child: Row(
        children: [
          UiAdaptiveNavBar(
            compactBreakpoint: _kCompactBreakpoint,
            semanticsId: 'desktop-adaptive-nav',
            items: _buildAdaptiveNavItems(visibleWindows.length),
            selectedIndex: _adaptiveSelectedIndex(topWindow),
            onSelected: (index) {
              switch (index) {
                case 0:
                  setState(() {
                    _allAppsOpen = false;
                    _themeStudioOpen = false;
                  });
                  _launchOrFocusApp('ino');
                case 1:
                  setState(() {
                    _allAppsOpen = false;
                    _themeStudioOpen = false;
                  });
                  unawaited(_showActiveAppsSheet(context, mruWindows));
                case 2:
                  setState(() {
                    _themeStudioOpen = false;
                    _allAppsOpen = !_allAppsOpen;
                  });
                case 3:
                  setState(() {
                    _allAppsOpen = false;
                    _themeStudioOpen = !_themeStudioOpen;
                  });
              }
            },
          ),
          Expanded(
            child: Stack(
              children: [
                Positioned.fill(
                  child: _buildSinglePaneContent(
                    topWindow: topWindow,
                    dockApps: dockApps,
                    openWidgets: openWidgets,
                    desk: desk,
                  ),
                ),
                _adaptiveTopActions(),
                ..._buildSharedOverlays(
                  session: session,
                  launcherApps: launcherApps,
                  includePresence: true,
                  includeModalOverlays: true,
                  themeStudioTop: 16,
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildAmbientGlows(UiColors c) => Positioned.fill(
    child: IgnorePointer(
      child: DecoratedBox(
        decoration: BoxDecoration(
          gradient: RadialGradient(
            center: const Alignment(-0.6, -0.7),
            radius: 0.9,
            colors: [
              c.accent.withValues(alpha: c.isDark ? 0.16 : 0.08),
              const Color(0x00000000),
            ],
          ),
        ),
        child: DecoratedBox(
          decoration: BoxDecoration(
            gradient: RadialGradient(
              center: const Alignment(0.7, 0.6),
              radius: 0.85,
              colors: [
                c.secondaryGlow.withValues(alpha: c.isDark ? 0.12 : 0.06),
                const Color(0x00000000),
              ],
            ),
          ),
        ),
      ),
    ),
  );

  @override
  Widget build(BuildContext context) {
    final session = BrainSessionScope.of(context);
    final projection = session.projection;
    final c = UiColors.of(context);

    if (projection == null) {
      if (session.desktopError != null) {
        return ColoredBox(
          color: c.surfacePage,
          child: Center(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Text('${session.desktopError}'),
                const SizedBox(height: 12),
                TextButton(
                  onPressed: () => unawaited(session.reloadDesktop()),
                  child: const Text('Retry'),
                ),
              ],
            ),
          ),
        );
      }
      return ColoredBox(
        color: c.surfacePage,
        child: const Center(child: CircularProgressIndicator()),
      );
    }

    final windows = projection.windows;
    final openWidgets = projection.openWidgets;
    final pinnedAppIds = {for (final pin in projection.pins) pin.toLowerCase()};
    final launcherApps = session.launcherApps;
    final dockApps = session.dockApps;

    final wc = _windows;
    final orderedWindows = wc.orderedWindows;
    final mruWindows = wc.mruWindows;
    final visibleWindows = wc.visibleWindows;
    final topWindow = wc.topWindow;
    final byId = wc.windowsById;
    final openAppIds = {for (final w in windows) w.appId};

    return LayoutBuilder(
      builder: (context, constraints) {
        final size = constraints.biggest;
        final isCompact = size.width < _kCompactBreakpoint;
        final isMedium =
            size.width >= _kCompactBreakpoint &&
            size.width < _kExpandedBreakpoint;
        final focusMode =
            size.width < _kExpandedBreakpoint || size.height < 640;
        final desk = Size(
          size.width,
          size.height - kOsBarHeight - 8 < 320.0
              ? 320.0
              : size.height - kOsBarHeight - 8,
        );
        _scheduleDeskSizeSync(desk);

        if (isCompact) {
          return _buildCompactShell(
            context: context,
            session: session,
            c: c,
            desk: desk,
            mruWindows: mruWindows,
            visibleWindows: visibleWindows,
            topWindow: topWindow,
            dockApps: dockApps,
            launcherApps: launcherApps,
            openWidgets: openWidgets,
          );
        }

        if (isMedium) {
          return _buildMediumShell(
            context: context,
            session: session,
            c: c,
            desk: desk,
            mruWindows: mruWindows,
            visibleWindows: visibleWindows,
            topWindow: topWindow,
            dockApps: dockApps,
            launcherApps: launcherApps,
            openWidgets: openWidgets,
          );
        }

        return DecoratedBox(
          decoration: BoxDecoration(color: c.surfacePage),
          child: Stack(
            clipBehavior: Clip.none,
            children: [
              if (c.isGlass) _buildAmbientGlows(c),

              // SmartDesktop (3D Orbital Neural Graph + Whiteboard Sticky Cards)
              Positioned(
                left: 0,
                top: 0,
                width: desk.width,
                height: desk.height,
                child: SmartDesktop(
                  hasOpenWindows: visibleWindows.isNotEmpty,
                  stylePreset: widget.stylePreset,
                  barLayout: widget.barLayout,
                  apps: dockApps,
                  totalAppsCount: launcherApps.length,
                  openCardIds: openWidgets,
                  onOpenCard: (cardId) => unawaited(session.openWidget(cardId)),
                  onCloseCard: (cardId) =>
                      unawaited(session.closeWidget(cardId)),
                  onLaunchApp: _launchOrFocusApp,
                  onOpenPalette: widget.onTogglePalette,
                  onOpenAllApps: () => setState(() {
                    _popover = DesktopPopoverKind.none;
                    _allAppsOpen = true;
                  }),
                  onSelectPreset: widget.onSelectPreset,
                  onCycleBarLayout: widget.onCycleBarLayout,
                  onAutoArrange: () => wc.autoArrange(desk),
                ),
              ),

              // Floating, Draggable, Resizable Acrylic Windows
              Positioned(
                left: 0,
                top: 0,
                width: desk.width,
                height: desk.height,
                child: Stack(
                  clipBehavior: Clip.none,
                  children: [
                    for (final spec in visibleWindows)
                      if (wc.stateFor(spec.id) case final winState?)
                        WindowFrame(
                          key: ValueKey('window:${spec.id}'),
                          window: winState,
                          focused: spec == topWindow,
                          forceZoom: focusMode,
                          desk: desk,
                          tabs: _buildWindowTabs(spec),
                          onFocus: () => wc.focusWindow(spec),
                          onClose: () => wc.closeWindow(spec),
                          onMinimize: () => wc.toggleMinimized(spec.id),
                          onNewTab: () => unawaited(session.newTab(spec.id)),
                          onFavourite: () =>
                              unawaited(session.toggleFavourite(spec.id)),
                          isFavourite: spec.isFavourite,
                          onPin: spec.appId == 'ino'
                              ? null
                              : () => wc.togglePinApp(spec.appId, pinnedAppIds),
                          isPinned: winState.isPinned,
                          onReload: () {
                            setState(() {});
                            unawaited(session.reloadDesktop());
                          },
                          onHardRestartSandbox: () => unawaited(
                            session.requestShellOperation(
                              spec.appId,
                              'sandbox-restart',
                              '',
                            ),
                          ),
                          onSwitchExecutionMode: (mode) => unawaited(
                            session.requestShellOperation(
                              spec.appId,
                              'switch-mode',
                              mode,
                            ),
                          ),
                          onInspectSandbox: () => _inspectSandbox(spec.appId),
                          onOpenAppSettings: () => _openAppGraph(spec.appId),
                          child: spec.appId == 'workspace'
                              ? SpatialAppCanvas(
                                  windows: [
                                    for (final w in visibleWindows)
                                      if (w.id != spec.id) w.id,
                                  ],
                                  windowStates: wc.windowStates,
                                  topWindowId: topWindow?.id,
                                  onFocusWindow: wc.focusWindowById,
                                  onCloseWindow: wc.closeWindowById,
                                  onToggleZoom: (id) =>
                                      wc.stateFor(id)?.toggleZoom(),
                                  onPinWindow: (id) {
                                    final target = byId[id];
                                    if (target != null) {
                                      wc.togglePinApp(
                                        target.appId,
                                        pinnedAppIds,
                                      );
                                    }
                                  },
                                  onReloadWindow: (id) {
                                    setState(() {});
                                    unawaited(session.reloadDesktop());
                                  },
                                  onHardRestartWindow: (id) {
                                    final target = byId[id];
                                    if (target != null) {
                                      unawaited(
                                        session.requestShellOperation(
                                          target.appId,
                                          'sandbox-restart',
                                          '',
                                        ),
                                      );
                                    }
                                  },
                                  onSwitchExecutionMode: (id, mode) {
                                    final target = byId[id];
                                    if (target != null) {
                                      unawaited(
                                        session.requestShellOperation(
                                          target.appId,
                                          'switch-mode',
                                          mode,
                                        ),
                                      );
                                    }
                                  },
                                  onInspectSandbox: (id) {
                                    final target = byId[id];
                                    if (target != null) {
                                      _inspectSandbox(target.appId);
                                    }
                                  },
                                  onOpenAppSettings: (id) {
                                    final target = byId[id];
                                    if (target != null) {
                                      _openAppGraph(target.appId);
                                    }
                                  },
                                  tabs: {
                                    for (final w in visibleWindows)
                                      if (w.id != spec.id)
                                        w.id: _buildWindowTabs(w),
                                  },
                                  windowBuilders: {
                                    for (final w in visibleWindows)
                                      if (w.id != spec.id)
                                        w.id: (context) => _buildWindowViewHost(
                                          w,
                                          keyPrefix: 'canvas-viewport',
                                        ),
                                  },
                                )
                              : _buildWindowViewHost(
                                  spec,
                                  keyPrefix: 'viewport',
                                ),
                        ),
                  ],
                ),
              ),

              // Ino Presence Layer (positioned over desktop area)
              if (session.smart)
                Positioned(
                  left: 0,
                  top: 0,
                  width: desk.width,
                  height: desk.height,
                  child: _buildPresenceLayer(session),
                ),

              // Adaptive Glass OS Bar & Dock
              Positioned(
                left: 0,
                right: 0,
                bottom: widget.barLayout == BarLayout.classicBottom ? 0 : 6,
                height: kOsBarHeight,
                child: OsBar(
                  barLayout: widget.barLayout,
                  stylePreset: widget.stylePreset,
                  apps: launcherApps,
                  openAppIds: openAppIds,
                  focusedAppId: topWindow?.appId,
                  paletteOpen: widget.paletteOpen,
                  allAppsOpen: _allAppsOpen,
                  showingDesktop: wc.showDesktopOnly || visibleWindows.isEmpty,
                  themeStudioOpen: _themeStudioOpen,
                  notifications: session.unreadNotifications,
                  presence: session.smart ? session.presenceState : null,
                  profileMenu: widget.profileMenu,
                  onTogglePalette: () {
                    setState(() {
                      _popover = DesktopPopoverKind.none;
                      _allAppsOpen = false;
                      _themeStudioOpen = false;
                    });
                    widget.onTogglePalette();
                  },
                  onToggleAllApps: () {
                    setState(() {
                      _popover = DesktopPopoverKind.none;
                      _themeStudioOpen = false;
                      _allAppsOpen = !_allAppsOpen;
                    });
                  },
                  onToggleShowDesktop: () {
                    setState(() {
                      _popover = DesktopPopoverKind.none;
                      _allAppsOpen = false;
                      _themeStudioOpen = false;
                    });
                    wc.toggleShowDesktop();
                  },
                  onCycleStylePreset: () {
                    final nextIdx =
                        (IcStylePreset.values.indexOf(widget.stylePreset) + 1) %
                        IcStylePreset.values.length;
                    widget.onSelectPreset(IcStylePreset.values[nextIdx]);
                  },
                  onToggleThemeStudio: () {
                    setState(() {
                      _popover = DesktopPopoverKind.none;
                      _allAppsOpen = false;
                      _themeStudioOpen = !_themeStudioOpen;
                    });
                  },
                  onLaunchApp: (appId) {
                    setState(() {
                      _popover = DesktopPopoverKind.none;
                      _allAppsOpen = false;
                    });
                    _launchOrFocusApp(appId);
                  },
                  onSecondaryTapApp: (appId, globalPos) => _togglePopover(
                    DesktopPopoverKind.dockMenu,
                    anchorX: globalPos.dx,
                    appId: appId,
                  ),
                  onHoverTooltip: (label, anchorX) {
                    if (_barTooltipLabel != label ||
                        _barTooltipAnchorX != anchorX) {
                      setState(() {
                        _barTooltipLabel = label;
                        _barTooltipAnchorX = anchorX;
                      });
                    }
                  },
                  onOpenSettings: () {
                    setState(() => _popover = DesktopPopoverKind.none);
                    _launchOrFocusApp('settings');
                  },
                  onOpenCompute: () =>
                      _togglePopover(DesktopPopoverKind.tokens),
                  onOpenNotifications: () =>
                      _togglePopover(DesktopPopoverKind.notifications),
                  onOpenClock: () => _togglePopover(DesktopPopoverKind.clock),
                ),
              ),

              // Hover Tooltip Above OsBar
              Positioned.fill(
                child: IgnorePointer(
                  child: BarTooltip(
                    screen: size,
                    barLayout: widget.barLayout,
                    label: _popover == DesktopPopoverKind.none
                        ? _barTooltipLabel
                        : null,
                    anchorX: _barTooltipAnchorX,
                  ),
                ),
              ),

              // OS Bar Popovers
              if (_popover != DesktopPopoverKind.none) ...[
                Positioned(
                  left: 0,
                  right: 0,
                  top: 0,
                  bottom: kOsBarHeight,
                  child: GestureDetector(
                    behavior: HitTestBehavior.opaque,
                    onTap: () =>
                        setState(() => _popover = DesktopPopoverKind.none),
                  ),
                ),
                DesktopBarPopover(
                  kind: _popover,
                  screen: size,
                  barLayout: widget.barLayout,
                  anchorX: _popoverAnchorX,
                  dockAppId: _dockMenuAppId,
                  dockAppTitle: _dockMenuAppId == null
                      ? null
                      : session.titleForAppId(_dockMenuAppId!),
                  notificationsCount: session.unreadNotifications,
                  notifications: session.notifications,
                  notificationsLoaded: session.notificationsLoaded,
                  notificationsLoading: session.notificationsLoading,
                  pinnedAppIds: pinnedAppIds,
                  windows: wc.windowSummaries(pinnedAppIds),
                  onClose: () =>
                      setState(() => _popover = DesktopPopoverKind.none),
                  onOpenSettings: () {
                    final targetApp = _dockMenuAppId;
                    setState(() => _popover = DesktopPopoverKind.none);
                    if (targetApp != null && targetApp.isNotEmpty) {
                      _openAppGraph(targetApp);
                    } else {
                      _launchOrFocusApp('settings');
                    }
                  },
                  onOpenComputeSettings: () {
                    if (widget.onOpenCompute != null) {
                      widget.onOpenCompute!();
                    } else {
                      _launchOrFocusApp('settings', settingsSection: 'billing');
                    }
                  },
                  onMarkNotificationRead: session.markNotificationRead,
                  onMarkAllNotificationsRead: session.markAllNotificationsRead,
                  onClearNotifications: session.clearNotifications,
                  onFocusWindow: wc.focusWindowById,
                  onNewWindowForApp: _launchOrFocusApp,
                  onTogglePinApp: (appId) =>
                      wc.togglePinApp(appId, pinnedAppIds),
                  onQuitApp: (appId) => unawaited(session.closeApp(appId)),
                ),
              ],

              // Alt+` Visual MRU Window Switcher
              if (_switcherOpen && orderedWindows.isNotEmpty)
                Positioned.fill(
                  child: AltBacktickWindowSwitcher(
                    windows: wc.windowSummaries(pinnedAppIds),
                    selectedIndex: _switcherIndex.clamp(
                      0,
                      orderedWindows.length - 1,
                    ),
                    onSelectWindow: (summary) {
                      setState(() => _switcherOpen = false);
                      wc.focusWindowById(summary.windowId);
                    },
                    onCancel: () => setState(() => _switcherOpen = false),
                  ),
                ),

              // Shared All Apps, Live Theme Studio, and Day-0 Guest Chrome Overlays
              ..._buildSharedOverlays(
                session: session,
                launcherApps: launcherApps,
                includePresence: false,
                includeModalOverlays: true,
                themeStudioBottom:
                    (widget.barLayout == BarLayout.classicBottom ? 0 : 6) +
                    kOsBarHeight +
                    8,
                themeStudioBackdropBottom: kOsBarHeight,
              ),
            ],
          ),
        );
      },
    );
  }
}
