import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import '../workspace/shell_activation_router.dart';
import '../workspace/spatial_app_canvas.dart';
import 'desktop_adaptive_shell.dart';
import 'desktop_overlays.dart';
import 'desktop_window_controller.dart';
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
  bool _switcherOpen = false;
  int _switcherIndex = 0;
  Size? _pendingDeskSize;

  BrainSessionController get _session => BrainSessionScope.readOf(context)!;
  DesktopWindowController get _windows => _windowController!;
  ShellActivationRouter get _router => widget.activationRouter ?? _localRouter!;

  DesktopGuestChromeConfig _guestChromeFor(String brainId) =>
      DesktopGuestChromeConfig(
        showPill: resolveIsGuestSession(
          isGuest: widget.isGuest ? true : null,
          isEphemeral: widget.isEphemeral ? true : null,
          brainId: brainId,
        ),
        remaining: widget.guestRemaining,
        onSaveBrain: widget.onSaveBrain,
        onDownloadApp: widget.onDownloadApp,
        onOpenUrl: widget.onOpenUrl,
        downloadHubUri: widget.downloadHubUri,
      );

  void _clearTransientOverlays() {
    setState(() {
      _popover = DesktopPopoverKind.none;
      _allAppsOpen = false;
      _themeStudioOpen = false;
    });
  }

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
        final isCompact = size.width < kDesktopCompactBreakpoint;
        final isMedium =
            size.width >= kDesktopCompactBreakpoint &&
            size.width < kDesktopExpandedBreakpoint;
        final focusMode =
            size.width < kDesktopExpandedBreakpoint || size.height < 640;
        final desk = Size(
          size.width,
          size.height - kOsBarHeight - 8 < 320.0
              ? 320.0
              : size.height - kOsBarHeight - 8,
        );
        _scheduleDeskSizeSync(desk);
        final guestChrome = _guestChromeFor(session.brainId);

        if (isCompact || isMedium) {
          return DesktopAdaptiveShell(
            mode: isCompact
                ? DesktopAdaptiveLayoutMode.compact
                : DesktopAdaptiveLayoutMode.medium,
            stylePreset: widget.stylePreset,
            barLayout: widget.barLayout,
            desk: desk,
            mruWindows: mruWindows,
            visibleWindows: visibleWindows,
            topWindow: topWindow,
            dockApps: dockApps,
            launcherApps: launcherApps,
            openWidgets: openWidgets,
            showDesktopOnly: wc.showDesktopOnly,
            allAppsOpen: _allAppsOpen,
            themeStudioOpen: _themeStudioOpen,
            presenceState: session.presenceState,
            profileMenu: widget.profileMenu,
            presenceLayer: !isCompact && session.smart
                ? _buildPresenceLayer(session)
                : null,
            buildWindowViewHost: (window) =>
                _buildWindowViewHost(window, keyPrefix: 'adaptive-viewport'),
            onLaunchApp: _launchOrFocusApp,
            onToggleShowDesktop: wc.toggleShowDesktop,
            onFocusWindow: wc.focusWindowById,
            onCloseWindow: wc.closeWindow,
            onOpenWidget: (cardId) => unawaited(session.openWidget(cardId)),
            onCloseWidget: (cardId) => unawaited(session.closeWidget(cardId)),
            onAutoArrange: () => wc.autoArrange(desk),
            onTogglePalette: widget.onTogglePalette,
            onSelectPreset: widget.onSelectPreset,
            onCycleBarLayout: widget.onCycleBarLayout,
            onSetAllAppsOpen: (open) => setState(() => _allAppsOpen = open),
            onSetThemeStudioOpen: (open) =>
                setState(() => _themeStudioOpen = open),
            onClearOverlays: _clearTransientOverlays,
            guestChrome: guestChrome,
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

              // Unified Desktop Chrome Overlays (Popovers, Alt+` Switcher,
              // All Apps, Live Theme Studio, and Day-0 Guest Chrome)
              Positioned.fill(
                child: DesktopChromeOverlayHost(
                  popover: _popover == DesktopPopoverKind.none
                      ? null
                      : DesktopPopoverOverlayConfig(
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
                          onClose: () => setState(
                            () => _popover = DesktopPopoverKind.none,
                          ),
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
                              _launchOrFocusApp(
                                'settings',
                                settingsSection: 'billing',
                              );
                            }
                          },
                          onMarkNotificationRead: session.markNotificationRead,
                          onMarkAllNotificationsRead:
                              session.markAllNotificationsRead,
                          onClearNotifications: session.clearNotifications,
                          onFocusWindow: wc.focusWindowById,
                          onNewWindowForApp: _launchOrFocusApp,
                          onTogglePinApp: (appId) =>
                              wc.togglePinApp(appId, pinnedAppIds),
                          onQuitApp: (appId) =>
                              unawaited(session.closeApp(appId)),
                        ),
                  windowSwitcher: _switcherOpen && orderedWindows.isNotEmpty
                      ? DesktopWindowSwitcherConfig(
                          windows: wc.windowSummaries(pinnedAppIds),
                          selectedIndex: _switcherIndex,
                          onSelectWindow: (summary) {
                            setState(() => _switcherOpen = false);
                            wc.focusWindowById(summary.windowId);
                          },
                          onCancel: () => setState(() => _switcherOpen = false),
                        )
                      : null,
                  allAppsOpen: _allAppsOpen,
                  launcherApps: launcherApps,
                  onCloseAllApps: () => setState(() => _allAppsOpen = false),
                  onLaunchApp: (appId) {
                    setState(() => _allAppsOpen = false);
                    _launchOrFocusApp(appId);
                  },
                  themeStudioOpen: _themeStudioOpen,
                  onCloseThemeStudio: () =>
                      setState(() => _themeStudioOpen = false),
                  themeStudioBottom:
                      (widget.barLayout == BarLayout.classicBottom ? 0 : 6) +
                      kOsBarHeight +
                      8,
                  themeStudioBackdropBottom: kOsBarHeight,
                  guestChrome: guestChrome,
                  onBeforeOpenDownloadModal: _clearTransientOverlays,
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}
