import 'dart:async';
import 'dart:math';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import '../src/widgets/ino/ino_orb_v2.dart';
import '../workspace/cross_window_router.dart';
import '../workspace/spatial_app_canvas.dart';
import '../workspace/workspace_app.dart' show UiThemeSeedsController;
import 'desktop_overlays.dart';
import 'guest_status_pill.dart';
import 'os_bar.dart';
import 'presence_layer.dart';
import 'smart_desktop.dart';
import 'window_frame.dart';

/// Live Desktop OS Shell & Floating Acrylic Window Manager backed by
/// [DesktopShellProjection] and [BrainSessionScope].
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

  @override
  State<LiveDesktopShell> createState() => _LiveDesktopShellState();
}

class _LiveDesktopShellState extends State<LiveDesktopShell> {
  static const double _kCompactBreakpoint = 600.0;
  static const double _kExpandedBreakpoint = 1024.0;

  final Map<String, DesktopWindowState> _windowStates = {};
  final List<String> _zOrder = [];
  String? _lastBrainId;
  String? _lastFocusedViewId;
  Map<String, SmartAppTileSpec> _catalogById = const {};

  bool _showDesktopOnly = false;
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
  BrainSessionController? _attachedSession;

  BrainSessionController get _session => BrainSessionScope.readOf(context)!;

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
    if (session.brainId.isNotEmpty && _lastBrainId != session.brainId) {
      _clearLocalWindowGeometry();
      _lastBrainId = session.brainId;
    }
    session.enableDesktopSession(true);
  }

  void _clearLocalWindowGeometry() {
    for (final state in _windowStates.values) {
      state.dispose();
    }
    _windowStates.clear();
    _zOrder.clear();
    _lastFocusedViewId = null;
  }

  @override
  void dispose() {
    HardwareKeyboard.instance.removeHandler(_onHardwareKey);
    _attachedSession?.enableDesktopSession(false, notify: false);
    _attachedSession = null;
    _clearLocalWindowGeometry();
    super.dispose();
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

  List<Widget> _buildGuestChromeOverlays(String brainId) => [
    if (_isGuestSession(brainId))
      Positioned(
        top: 10,
        left: 16,
        right: 16,
        child: Align(
          alignment: Alignment.topCenter,
          child: GuestStatusPill(
            remaining: widget.guestRemaining,
            onSaveBrain: widget.onSaveBrain,
            onDownloadApp: _handleDownloadAppTap,
          ),
        ),
      ),
    if (_downloadModalOpen)
      Positioned.fill(
        child: DownloadAppModal(
          onClose: () => setState(() => _downloadModalOpen = false),
          onOpenUrl: widget.onOpenUrl,
          downloadHubUri: widget.downloadHubUri,
        ),
      ),
  ];

  /// Ensures pinned windows (`isPinned == true`) stay above unpinned windows in `_zOrder`.
  void _normalizeZOrder() {
    final seen = <String>{};
    final unpinned = <String>[];
    final pinned = <String>[];
    for (final id in _zOrder) {
      if (!seen.add(id)) continue;
      if (_windowStates[id]?.isPinned ?? false) {
        pinned.add(id);
      } else {
        unpinned.add(id);
      }
    }
    _zOrder
      ..clear()
      ..addAll(unpinned)
      ..addAll(pinned);
  }

  List<DesktopWindowProjection> _currentMruWindows() {
    final windows = _session.projection?.windows ?? const [];
    final byId = {for (final w in windows) w.id: w};
    return _zOrder
        .map((id) => byId[id])
        .whereType<DesktopWindowProjection>()
        .toList()
        .reversed
        .toList();
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
        final mru = _currentMruWindows();
        setState(() => _switcherOpen = false);
        if (mru.isNotEmpty) {
          _focusWindow(mru[_switcherIndex.clamp(0, mru.length - 1)]);
        }
        return true;
      }
      return false;
    }

    if (e is KeyUpEvent) return false;

    if (kb.isAltPressed &&
        (e.physicalKey == PhysicalKeyboardKey.backquote ||
            e.logicalKey == LogicalKeyboardKey.backquote)) {
      final mru = _currentMruWindows();
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

  String _titleForAppId(String appId) {
    final normalized = appId.trim().toLowerCase();
    if (_catalogById[normalized] case final spec? when spec.name.isNotEmpty) {
      return spec.name;
    }
    return IcAppIcon.humanizeAppTitle(appId);
  }

  Rect _initialWindowBounds(int index, Size desk) {
    final w = (desk.width * 0.64).clamp(680.0, 980.0);
    final h = (desk.height * 0.78).clamp(460.0, 680.0);
    final baseLeft = desk.width > 1180
        ? (desk.width - w - 36).clamp(380.0, desk.width - w - 24)
        : ((desk.width - w) / 2).clamp(16.0, max(16.0, desk.width - w - 16));
    final baseTop = ((desk.height - h) / 2 - 12).clamp(
      18.0,
      max(18.0, desk.height - h - 18),
    );
    final offset = (index % 5) * 28.0;
    return Rect.fromLTWH(
      (baseLeft - offset).clamp(16.0, max(16.0, desk.width - w - 16)),
      (baseTop + offset).clamp(16.0, max(16.0, desk.height - h - 16)),
      w,
      h,
    );
  }

  bool _shouldLaunchMaximized(String appId) =>
      _catalogById[appId.trim().toLowerCase()]?.launch == AppLaunch.maximized;

  void _syncWindowStates(
    String brainId,
    List<DesktopWindowProjection> windows,
    String? focusedViewId,
    Size desk,
  ) {
    for (var i = 0; i < windows.length; i++) {
      final spec = windows[i];
      final existing = _windowStates[spec.id];
      if (existing == null) {
        _windowStates[spec.id] = DesktopWindowState(
          id: spec.numericId,
          appId: spec.appId,
          title: spec.title,
          neuronUri: 'neuron://$brainId/apps/${spec.appId}',
          bounds: _initialWindowBounds(i, desk),
          zoomed: _shouldLaunchMaximized(spec.appId),
        );
        if (!_zOrder.contains(spec.id)) {
          _zOrder.add(spec.id);
        }
      } else {
        existing.title = spec.title;
        if (!_zOrder.contains(spec.id)) {
          _zOrder.add(spec.id);
        }
      }
    }

    final activeIds = {for (final w in windows) w.id};
    _windowStates.removeWhere((id, state) {
      final gone = !activeIds.contains(id);
      if (gone) state.dispose();
      return gone;
    });
    _zOrder.removeWhere((id) => !activeIds.contains(id));

    if (focusedViewId != null && focusedViewId != _lastFocusedViewId) {
      _lastFocusedViewId = focusedViewId;
      for (final w in windows) {
        if (w.activeTabId == focusedViewId ||
            w.tabs.any((t) => t.id == focusedViewId)) {
          _zOrder.remove(w.id);
          _zOrder.add(w.id);
          break;
        }
      }
    }
    _normalizeZOrder();
  }

  void _focusWindow(DesktopWindowProjection spec) {
    final state = _windowStates[spec.id];
    if (state != null && state.minimized) {
      state.setMinimized(false);
    }
    setState(() {
      _showDesktopOnly = false;
      _zOrder.remove(spec.id);
      _zOrder.add(spec.id);
      _normalizeZOrder();
    });
    unawaited(_session.focusWindow(spec.id));
  }

  void _closeWindow(DesktopWindowProjection spec) {
    unawaited(_session.closeWindow(spec.id, tabIndex: spec.activeTabIndex));
  }

  void _togglePinApp(String appId, Set<String> pinnedAppIds) {
    if (pinnedAppIds.contains(appId.toLowerCase())) {
      unawaited(_session.unpinApp(appId));
    } else {
      unawaited(_session.pinApp(appId));
    }
  }

  /// Auto-arranges unpinned windows into a clean grid while locking pinned windows.
  void _autoArrangeWindows(Size desk) {
    final unpinnedStates = <DesktopWindowState>[
      for (final id in _zOrder)
        if (_windowStates[id] != null &&
            !_windowStates[id]!.minimized &&
            !_windowStates[id]!.isPinned)
          _windowStates[id]!,
    ];
    if (unpinnedStates.isEmpty) return;

    final cols = unpinnedStates.length == 1 ? 1 : 2;
    final rows = (unpinnedStates.length / cols).ceil();
    const pad = 24.0;
    const gap = 16.0;
    final cellW = ((desk.width - pad * 2 - gap * (cols - 1)) / cols).clamp(
      360.0,
      960.0,
    );
    final cellH = ((desk.height - pad * 2 - gap * (rows - 1)) / rows).clamp(
      260.0,
      680.0,
    );

    setState(() {
      for (var i = 0; i < unpinnedStates.length; i++) {
        final col = i % cols;
        final row = i ~/ cols;
        unpinnedStates[i].setBounds(
          Rect.fromLTWH(
            pad + col * (cellW + gap),
            pad + row * (cellH + gap),
            cellW,
            cellH,
          ),
        );
      }
      _normalizeZOrder();
    });
  }

  void _handleChildActivation(
    Map<String, dynamic> activation,
    List<DesktopWindowProjection> windows, {
    String? sourceAppId,
  }) {
    final brain = _session.brainId;
    if (activation['kind'] == 'os_uri') {
      final uriStr = '${activation['uri'] ?? ''}';
      final fallbackFrom = sourceAppId != null && sourceAppId.isNotEmpty
          ? '$brain/apps/$sourceAppId'
          : null;
      final route = openWindowRouteOf(
        uriStr,
        intent: activation['intent'] as String?,
        fallbackFromNeuron: fallbackFrom,
      );
      if (route != null) {
        _launchOrFocusApp(
          route.app,
          windows,
          settingsSection: route.section,
          openInput: route.input,
          openRoute: route,
        );
        return;
      }
      final uri = Uri.tryParse(uriStr);
      if (uriStr.contains('settings')) {
        _launchOrFocusApp(
          'settings',
          windows,
          settingsSection: uri?.queryParameters['section'],
        );
        return;
      }
      return;
    }
    final targetApp = activation['application'] as String?;
    if (targetApp != null && targetApp.isNotEmpty) {
      _launchOrFocusApp(
        targetApp,
        windows,
        settingsSection: activation['tab'] as String?,
      );
    }
    _session.activate({'brainId': brain, ...activation});
  }

  Future<void> _dispatchWindowNavigateTo(
    String appId, {
    String? windowId,
    String? input,
    String? argsJson,
    required String transition,
    String? fromNeuron,
  }) async {
    final delivered = await dispatchNavigateTo(
      sendAct: _session.sendAction,
      targetNeuron: '${_session.brainId}/apps/$appId',
      fromNeuron: fromNeuron,
      argsJson: argsJson,
      transition: transition,
      input: input,
    );
    if (!delivered || !mounted) return;
    setState(() {
      if (windowId != null && _windowStates.containsKey(windowId)) {
        _windowStates[windowId]?.reloadEpoch++;
      } else {
        for (final st in _windowStates.values) {
          if (st.appId == appId) st.reloadEpoch++;
        }
      }
    });
  }

  void _openAppGraph(String appId, List<DesktopWindowProjection> windows) =>
      _launchOrFocusApp(
        'creator',
        windows,
        openInput: 'node:${_session.brainId}/apps/$appId',
      );

  void _launchOrFocusApp(
    String appId,
    List<DesktopWindowProjection> windows, {
    String? settingsSection,
    String? openInput,
    OpenWindowRoute? openRoute,
  }) {
    setState(() => _showDesktopOnly = false);

    final session = _session;
    final effectiveInput =
        openRoute?.input ??
        (openInput != null && openInput.isNotEmpty ? openInput : null);
    final argsJson = openRoute?.argsJson;
    final transition = openRoute?.transition ?? 'fade-through';
    final fromNeuron = openRoute?.fromNeuron;
    final shouldNavigateTo =
        effectiveInput != null ||
        argsJson != null ||
        (openRoute?.hasExplicitTransition ?? false);
    if (appId == 'settings' &&
        !shouldNavigateTo &&
        settingsSection != null &&
        settingsSection.isNotEmpty) {
      unawaited(
        session.requestShellOperation('settings', 'navigate', settingsSection),
      );
    }

    for (final w in windows.reversed) {
      if (w.appId == appId) {
        _focusWindow(w);
        if (shouldNavigateTo) {
          unawaited(
            _dispatchWindowNavigateTo(
              appId,
              windowId: w.id,
              input: effectiveInput,
              argsJson: argsJson,
              transition: transition,
              fromNeuron: fromNeuron,
            ),
          );
          session.activate({
            'brainId': session.brainId,
            'kind': 'workspace',
            'application': appId,
            'operation': 'open',
            'input': ?effectiveInput,
            'argsJson': ?argsJson,
            'transition': transition,
            'fromNeuron': ?fromNeuron,
            'navigatedTo': true,
          });
          setState(() => _windowStates[w.id]?.reloadEpoch++);
        }
        return;
      }
    }

    unawaited(session.openApp(appId));

    if (appId == 'settings' && !shouldNavigateTo) {
      session.activate({
        'kind': 'settings',
        'brainId': session.brainId,
        'tab': ?settingsSection,
      });
      return;
    }
    if (shouldNavigateTo) {
      unawaited(
        _dispatchWindowNavigateTo(
          appId,
          input: effectiveInput,
          argsJson: argsJson,
          transition: transition,
          fromNeuron: fromNeuron,
        ),
      );
    }
    session.activate({
      'brainId': session.brainId,
      'kind': 'workspace',
      'application': appId,
      'operation': 'open',
      'input': ?effectiveInput,
      'argsJson': ?argsJson,
      if (shouldNavigateTo) 'transition': transition,
      'fromNeuron': ?fromNeuron,
      if (shouldNavigateTo) 'navigatedTo': true,
    });
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
    DesktopWindowProjection spec,
    List<DesktopWindowProjection> windows, {
    required String keyPrefix,
  }) => ViewHost(
    key: ValueKey(
      '$keyPrefix:${spec.viewportUri}:${_windowStates[spec.id]?.reloadEpoch ?? 0}',
    ),
    kind: 'viewport',
    name: spec.viewportUri,
    signals: _session.journalFor(spec.viewportUri),
    onActivate: (activation) =>
        _handleChildActivation(activation, windows, sourceAppId: spec.appId),
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

  Widget _adaptiveTopActions(List<DesktopWindowProjection> windows) =>
      Positioned(
        top: 8,
        right: 8,
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            IconButton(
              tooltip: 'Home',
              isSelected: _showDesktopOnly,
              icon: const Icon(LucideIcons.brainCircuit),
              onPressed: () => setState(() {
                _popover = DesktopPopoverKind.none;
                _allAppsOpen = false;
                _themeStudioOpen = false;
                _showDesktopOnly = !_showDesktopOnly;
              }),
            ),
            IconButton(
              tooltip: 'Settings',
              icon: const Icon(LucideIcons.settings),
              onPressed: () {
                setState(() => _popover = DesktopPopoverKind.none);
                _launchOrFocusApp('settings', windows);
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
    required List<DesktopWindowProjection> windows,
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
        onLaunchApp: (appId) => _launchOrFocusApp(appId, windows),
        onOpenPalette: widget.onTogglePalette,
        onOpenAllApps: () => setState(() {
          _popover = DesktopPopoverKind.none;
          _allAppsOpen = true;
        }),
        onSelectPreset: widget.onSelectPreset,
        onCycleBarLayout: widget.onCycleBarLayout,
        onAutoArrange: () => _autoArrangeWindows(desk),
      );
    }

    return _buildWindowViewHost(
      topWindow,
      windows,
      keyPrefix: 'adaptive-viewport',
    );
  }

  Future<void> _showActiveAppsSheet(
    BuildContext context,
    List<DesktopWindowProjection> orderedWindows,
    Map<String, DesktopWindowProjection> byId,
  ) {
    final activeList = orderedWindows.reversed.toList(growable: false);
    return showUiSheet<void>(
      context: context,
      compactBreakpoint: _kCompactBreakpoint,
      builder: (sheetCtx) {
        final uiColors = UiColors.of(sheetCtx);
        return UiSheet(
          title: 'Active Apps',
          subtitle: activeList.isEmpty
              ? 'No open apps'
              : '${activeList.length} running',
          showDragHandle: true,
          semanticsId: 'adaptive-sheet:active-apps',
          child: activeList.isEmpty
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
                    itemCount: activeList.length,
                    separatorBuilder: (_, _) => const SizedBox(height: 6),
                    itemBuilder: (_, idx) {
                      final w = activeList[idx];
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
                            _closeWindow(w);
                          },
                        ),
                        onTap: () {
                          Navigator.of(sheetCtx).pop();
                          final target = byId[w.id];
                          if (target != null) _focusWindow(target);
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
    List<DesktopWindowProjection> windows,
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
                    _launchOrFocusApp(spec.id, windows);
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
    required List<DesktopWindowProjection> windows,
    required Map<String, DesktopWindowProjection> byId,
    required List<DesktopWindowProjection> orderedWindows,
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
                        windows: windows,
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
                            _launchOrFocusApp('ino', windows);
                          },
                        ),
                      ),
                    _adaptiveTopActions(windows),
                    ..._buildGuestChromeOverlays(session.brainId),
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
                      _launchOrFocusApp('ino', windows);
                    case 1:
                      unawaited(
                        _showActiveAppsSheet(compactCtx, orderedWindows, byId),
                      );
                    case 2:
                      unawaited(
                        _showAllAppsSheet(compactCtx, launcherApps, windows),
                      );
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
    required List<DesktopWindowProjection> windows,
    required Map<String, DesktopWindowProjection> byId,
    required List<DesktopWindowProjection> orderedWindows,
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
                  _launchOrFocusApp('ino', windows);
                case 1:
                  setState(() {
                    _allAppsOpen = false;
                    _themeStudioOpen = false;
                  });
                  unawaited(
                    _showActiveAppsSheet(context, orderedWindows, byId),
                  );
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
                    windows: windows,
                    dockApps: dockApps,
                    openWidgets: openWidgets,
                    desk: desk,
                  ),
                ),
                if (session.smart)
                  Positioned.fill(
                    child: PresenceLayer(
                      presence: session.presenceStream,
                      enabled: session.smart,
                      onOpenIno: () => _launchOrFocusApp('ino', windows),
                      onAction: (action) {
                        if (action.kind == 'open' ||
                            action.kind == 'navigate') {
                          _launchOrFocusApp(action.argument, windows);
                        }
                      },
                    ),
                  ),
                _adaptiveTopActions(windows),
                ..._buildGuestChromeOverlays(session.brainId),
                if (_allAppsOpen)
                  Positioned.fill(
                    child: AllAppsLauncherOverlay(
                      apps: launcherApps,
                      onClose: () => setState(() => _allAppsOpen = false),
                      onLaunchApp: (appId) {
                        setState(() => _allAppsOpen = false);
                        _launchOrFocusApp(appId, windows);
                      },
                    ),
                  ),
                if (_themeStudioOpen) ...[
                  Positioned.fill(
                    child: GestureDetector(
                      behavior: HitTestBehavior.opaque,
                      onTap: () => setState(() => _themeStudioOpen = false),
                    ),
                  ),
                  Positioned(
                    right: 16,
                    top: 16,
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(maxWidth: 340),
                      child: Builder(
                        builder: (ctx) {
                          final controller = UiThemeSeedsController.maybeOf(
                            ctx,
                          );
                          return ThemeStudioPanel(
                            seeds: controller?.seeds ?? UiThemeSeeds.warmPaper,
                            onChanged: controller?.setSeeds ?? (_) {},
                          );
                        },
                      ),
                    ),
                  ),
                ],
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

  List<DesktopNotificationEntry> _notificationEntries(
    BrainSessionController session,
  ) => [
    for (final item in session.notifications)
      DesktopNotificationEntry(
        id: item.id,
        sourceApp: item.sourceApp,
        title: item.title,
        body: item.body,
        priority: item.priority.index,
        read: item.read,
        createdUtc: item.createdUtc,
        actionUrl: item.actionUrl,
      ),
  ];

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

    final brainId = session.brainId;
    final windows = projection.windows;
    final openWidgets = projection.openWidgets;
    final pinnedAppIds = {for (final pin in projection.pins) pin.toLowerCase()};

    final launcherApps = <SmartAppTileSpec>[];
    final launcherSeen = <String>{};
    for (final tile in projection.tiles) {
      final norm = tile.id.toLowerCase();
      if (norm.isNotEmpty && launcherSeen.add(norm)) {
        launcherApps.add(
          SmartAppTileSpec.fromProjectionTile(tile, brain: brainId),
        );
      }
    }
    for (final w in windows) {
      final norm = w.appId.toLowerCase();
      if (norm.isNotEmpty && launcherSeen.add(norm)) {
        launcherApps.add(
          SmartAppTileSpec.fromIconSpec(
            w.appId,
            titleOverride: w.title,
            brain: brainId,
          ),
        );
      }
    }
    for (final id in [...projection.pins, ...session.installedApps]) {
      final norm = id.trim().toLowerCase();
      if (norm.isNotEmpty && launcherSeen.add(norm)) {
        launcherApps.add(SmartAppTileSpec.fromIconSpec(norm, brain: brainId));
      }
    }

    final catalogById = <String, SmartAppTileSpec>{
      for (final spec in launcherApps) spec.id.toLowerCase(): spec,
    };
    _catalogById = catalogById;

    final dockSeen = <String>{};
    final dockApps = <SmartAppTileSpec>[];
    for (final spec in launcherApps) {
      if (spec.dock != null && dockSeen.add(spec.id.toLowerCase())) {
        dockApps.add(spec);
      }
    }
    for (final pin in projection.pins) {
      final norm = pin.toLowerCase();
      if (dockSeen.add(norm)) {
        dockApps.add(
          catalogById[norm] ??
              SmartAppTileSpec.fromIconSpec(pin, brain: brainId),
        );
      }
    }
    for (final spec in launcherApps) {
      if (spec.appKind == 'unit' && dockSeen.add(spec.id.toLowerCase())) {
        dockApps.add(spec);
      }
    }
    for (final w in windows) {
      final norm = w.appId.toLowerCase();
      if (dockSeen.add(norm)) {
        dockApps.add(
          catalogById[norm] ??
              SmartAppTileSpec.fromIconSpec(
                w.appId,
                titleOverride: w.title,
                brain: brainId,
              ),
        );
      }
    }

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
          max(320.0, size.height - kOsBarHeight - 8),
        );
        _syncWindowStates(brainId, windows, projection.focusedViewId, desk);

        final byId = {for (final w in windows) w.id: w};
        final orderedWindows = _zOrder
            .map((id) => byId[id])
            .whereType<DesktopWindowProjection>()
            .toList();
        final visibleWindows = _showDesktopOnly
            ? const <DesktopWindowProjection>[]
            : [
                for (final w in orderedWindows)
                  if (!(_windowStates[w.id]?.minimized ?? false)) w,
              ];
        final topWindow = visibleWindows.isEmpty ? null : visibleWindows.last;
        final openAppIds = {for (final w in windows) w.appId};

        if (isCompact) {
          return _buildCompactShell(
            context: context,
            session: session,
            c: c,
            desk: desk,
            windows: windows,
            byId: byId,
            orderedWindows: orderedWindows,
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
            windows: windows,
            byId: byId,
            orderedWindows: orderedWindows,
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
                  onLaunchApp: (appId) => _launchOrFocusApp(appId, windows),
                  onOpenPalette: widget.onTogglePalette,
                  onOpenAllApps: () => setState(() {
                    _popover = DesktopPopoverKind.none;
                    _allAppsOpen = true;
                  }),
                  onSelectPreset: widget.onSelectPreset,
                  onCycleBarLayout: widget.onCycleBarLayout,
                  onAutoArrange: () => _autoArrangeWindows(desk),
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
                      WindowFrame(
                        key: ValueKey('window:${spec.id}'),
                        window: _windowStates[spec.id]!,
                        focused: spec == topWindow,
                        forceZoom: focusMode,
                        desk: desk,
                        tabs: _buildWindowTabs(spec),
                        onFocus: () => _focusWindow(spec),
                        onClose: () => _closeWindow(spec),
                        onMinimize: () {
                          final st = _windowStates[spec.id];
                          if (st != null) {
                            setState(() => st.setMinimized(!st.minimized));
                          }
                        },
                        onNewTab: () => unawaited(session.newTab(spec.id)),
                        onFavourite: () =>
                            unawaited(session.toggleFavourite(spec.id)),
                        isFavourite: spec.isFavourite,
                        onPin: spec.appId == 'ino'
                            ? null
                            : () {
                                setState(() => _normalizeZOrder());
                                _togglePinApp(spec.appId, pinnedAppIds);
                              },
                        isPinned:
                            _windowStates[spec.id]?.isPinned ?? spec.isPinned,
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
                        onInspectSandbox: () {
                          _launchOrFocusApp('creator', windows);
                          session.activate({
                            'brainId': brainId,
                            'kind': 'workspace',
                            'application': 'creator',
                            'operation': 'inspect',
                            'resource': 'app.${spec.appId}',
                          });
                        },
                        onOpenAppSettings: () =>
                            _openAppGraph(spec.appId, windows),
                        child: spec.appId == 'workspace'
                            ? SpatialAppCanvas(
                                windows: [
                                  for (final w in visibleWindows)
                                    if (w.id != spec.id) w.id,
                                ],
                                windowStates: _windowStates,
                                topWindowId: topWindow?.id,
                                onFocusWindow: (id) {
                                  final target = byId[id];
                                  if (target != null) _focusWindow(target);
                                },
                                onCloseWindow: (id) {
                                  final target = byId[id];
                                  if (target != null) _closeWindow(target);
                                },
                                onToggleZoom: (id) =>
                                    _windowStates[id]?.toggleZoom(),
                                onPinWindow: (id) {
                                  final target = byId[id];
                                  if (target != null) {
                                    setState(() => _normalizeZOrder());
                                    _togglePinApp(target.appId, pinnedAppIds);
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
                                    _launchOrFocusApp('creator', windows);
                                    session.activate({
                                      'brainId': brainId,
                                      'kind': 'workspace',
                                      'application': 'creator',
                                      'operation': 'inspect',
                                      'resource': 'app.${target.appId}',
                                    });
                                  }
                                },
                                onOpenAppSettings: (id) {
                                  final target = byId[id];
                                  if (target != null) {
                                    _openAppGraph(target.appId, windows);
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
                                        windows,
                                        keyPrefix: 'canvas-viewport',
                                      ),
                                },
                              )
                            : _buildWindowViewHost(
                                spec,
                                windows,
                                keyPrefix: 'viewport',
                              ),
                      ),
                  ],
                ),
              ),

              // Ino Presence Layer
              if (session.smart)
                Positioned(
                  left: 0,
                  top: 0,
                  width: desk.width,
                  height: desk.height,
                  child: PresenceLayer(
                    presence: session.presenceStream,
                    enabled: session.smart,
                    onOpenIno: () => _launchOrFocusApp('ino', windows),
                    onAction: (action) {
                      if (action.kind == 'open' || action.kind == 'navigate') {
                        _launchOrFocusApp(action.argument, windows);
                      }
                    },
                  ),
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
                  showingDesktop: _showDesktopOnly || visibleWindows.isEmpty,
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
                      _showDesktopOnly = !_showDesktopOnly;
                    });
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
                    _launchOrFocusApp(appId, windows);
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
                    _launchOrFocusApp('settings', windows);
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
                      : _titleForAppId(_dockMenuAppId!),
                  notificationsCount: session.unreadNotifications,
                  notifications: _notificationEntries(session),
                  notificationsLoaded: session.notificationsLoaded,
                  notificationsLoading: session.notificationsLoading,
                  pinnedAppIds: pinnedAppIds,
                  windows: [
                    for (final w in orderedWindows.reversed)
                      DesktopWindowSummary(
                        windowId: w.id,
                        numericId: w.numericId,
                        appId: w.appId,
                        title: w.title,
                        minimized: _windowStates[w.id]?.minimized ?? false,
                        isPinned: pinnedAppIds.contains(w.appId.toLowerCase()),
                      ),
                  ],
                  onClose: () =>
                      setState(() => _popover = DesktopPopoverKind.none),
                  onOpenSettings: () {
                    final targetApp = _dockMenuAppId;
                    setState(() => _popover = DesktopPopoverKind.none);
                    if (targetApp != null && targetApp.isNotEmpty) {
                      _openAppGraph(targetApp, windows);
                    } else {
                      _launchOrFocusApp('settings', windows);
                    }
                  },
                  onOpenComputeSettings: () {
                    if (widget.onOpenCompute != null) {
                      widget.onOpenCompute!();
                    } else {
                      _launchOrFocusApp(
                        'settings',
                        windows,
                        settingsSection: 'billing',
                      );
                    }
                  },
                  onMarkNotificationRead: session.markNotificationRead,
                  onMarkAllNotificationsRead: session.markAllNotificationsRead,
                  onClearNotifications: session.clearNotifications,
                  onFocusWindow: (winId) {
                    final target = byId[winId];
                    if (target != null) _focusWindow(target);
                  },
                  onNewWindowForApp: (appId) =>
                      _launchOrFocusApp(appId, windows),
                  onTogglePinApp: (appId) => _togglePinApp(appId, pinnedAppIds),
                  onQuitApp: (appId) => unawaited(session.closeApp(appId)),
                ),
              ],

              // Alt+` Visual MRU Window Switcher
              if (_switcherOpen && orderedWindows.isNotEmpty)
                Positioned.fill(
                  child: AltBacktickWindowSwitcher(
                    windows: [
                      for (final w in orderedWindows.reversed)
                        DesktopWindowSummary(
                          windowId: w.id,
                          numericId: w.numericId,
                          appId: w.appId,
                          title: w.title,
                          minimized: _windowStates[w.id]?.minimized ?? false,
                          isPinned: pinnedAppIds.contains(
                            w.appId.toLowerCase(),
                          ),
                        ),
                    ],
                    selectedIndex: _switcherIndex.clamp(
                      0,
                      orderedWindows.length - 1,
                    ),
                    onSelectWindow: (summary) {
                      setState(() => _switcherOpen = false);
                      final target = byId[summary.windowId];
                      if (target != null) _focusWindow(target);
                    },
                    onCancel: () => setState(() => _switcherOpen = false),
                  ),
                ),

              // All Apps Catalog Launcher Overlay
              if (_allAppsOpen)
                Positioned.fill(
                  child: AllAppsLauncherOverlay(
                    apps: launcherApps,
                    onClose: () => setState(() => _allAppsOpen = false),
                    onLaunchApp: (appId) {
                      setState(() => _allAppsOpen = false);
                      _launchOrFocusApp(appId, windows);
                    },
                  ),
                ),

              // Live Theme Studio Overlay
              if (_themeStudioOpen) ...[
                Positioned(
                  left: 0,
                  right: 0,
                  top: 0,
                  bottom: kOsBarHeight,
                  child: GestureDetector(
                    behavior: HitTestBehavior.opaque,
                    onTap: () => setState(() => _themeStudioOpen = false),
                  ),
                ),
                Positioned(
                  right: 16,
                  bottom:
                      (widget.barLayout == BarLayout.classicBottom ? 0 : 6) +
                      kOsBarHeight +
                      8,
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 340),
                    child: Builder(
                      builder: (context) {
                        final controller = UiThemeSeedsController.of(context);
                        return ThemeStudioPanel(
                          seeds: controller.seeds,
                          onChanged: controller.setSeeds,
                        );
                      },
                    ),
                  ),
                ),
              ],

              // Day-0 Guest Status Pill & Download Modal
              ..._buildGuestChromeOverlays(brainId),
            ],
          ),
        );
      },
    );
  }
}
