import 'dart:async';
import 'dart:math';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import '../src/widgets/ino/ino_orb_v2.dart';
import '../workspace/cross_window_router.dart';
import '../workspace/spatial_app_canvas.dart';
import '../workspace/workspace_app.dart';
import 'desktop_overlays.dart';
import 'os_bar.dart';
import 'presence_layer.dart';
import 'smart_desktop.dart';
import 'window_frame.dart';

class _ParsedDesktopWindow {
  const _ParsedDesktopWindow({
    required this.windowId,
    required this.numericId,
    required this.appId,
    required this.title,
    required this.neuronUri,
    required this.viewportRef,
    required this.tabs,
    this.focusNode,
    this.newTabNode,
    this.starNode,
    this.pinNode,
    this.closeNode,
  });

  final String windowId;
  final int numericId;
  final String appId;
  final String title;
  final String neuronUri;
  final Map<String, dynamic> viewportRef;
  final List<Map<String, dynamic>> tabs;
  final Map<String, dynamic>? focusNode;
  final Map<String, dynamic>? newTabNode;
  final Map<String, dynamic>? starNode;
  final Map<String, dynamic>? pinNode;
  final Map<String, dynamic>? closeNode;
}

/// Live Desktop OS Shell & Floating Acrylic Window Manager backed 100% by
/// `{brain}/apps/desktop/view` and live kernel viewports.
class LiveDesktopShell extends StatefulWidget {
  const LiveDesktopShell({
    super.key,
    required this.brain,
    required this.name,
    required this.load,
    required this.watch,
    required this.onAction,
    required this.onActivate,
    required this.stylePreset,
    required this.barLayout,
    required this.paletteOpen,
    required this.onTogglePalette,
    required this.onSelectPreset,
    required this.onCycleBarLayout,
    this.onOpenCompute,
    this.scenes,
    this.entries,
    this.journal,
    this.localPreferences,
    this.profileMenu,
    this.read,
    this.watchSignal,
    this.callNeuron,
    this.requestApp,
    this.notificationsClient,
    this.installedApps = const <String>[],
    this.onDynamicAppsChanged,
    this.transcribe,
    this.talk,
    this.speak,
    required this.knowsApp,
  });

  final String brain;
  final String name;
  final NeuronLoader load;
  final Stream<void> Function(String kind, String name)? watch;
  final NeuronAction onAction;
  final ValueChanged<Map<String, dynamic>> onActivate;
  final IcStylePreset stylePreset;
  final BarLayout barLayout;
  final bool paletteOpen;
  final VoidCallback onTogglePalette;
  final ValueChanged<IcStylePreset> onSelectPreset;
  final VoidCallback onCycleBarLayout;
  final VoidCallback? onOpenCompute;
  final SceneClient? scenes;
  final EntriesClient Function(String brainId)? entries;

  /// The brain's journal tail after a sequence; the Creator's window draws it as pulses on its brain graph.
  final Stream<List<JournalTailSignal>> Function(String brainId, int since)?
  journal;
  final LocalPreferences? localPreferences;
  final Widget? profileMenu;

  /// Sends an operation to an app (POST …/apps/{app}/request): the shell's own desktop gestures that no
  /// view node carries (open a widget, close an app) and a sandbox's controls.
  final Future<void> Function(String app, String operation, String input)?
  requestApp;

  /// The apps the shell knows: an OpenWindow intent from a window's view may open only these.
  final bool Function(String app) knowsApp;

  /// A read-only contract call (contract, key, method) answering its decoded result; smart mode stays off
  /// without it.
  final Future<Map<String, dynamic>?> Function(
    String contract,
    String key,
    String method,
  )?
  read;

  /// Watches one signal type on a neuron contract, yielding decoded payloads.
  final NeuronSignalWatcher? watchSignal;

  /// Invokes any typed method on a neuron contract over `NeuronChannel` (gRPC).
  final NeuronMethodCaller? callNeuron;

  /// Optional client for real OS notifications; falls back to [entries] when present.
  final IntoChatOSUiClient? notificationsClient;

  /// Optional list of brain-installed / runtime-deployed app IDs to surface in the Launcher & Dock.
  final List<String> installedApps;

  /// Optional callback notified whenever runtime apps discovered on the desktop change.
  final ValueChanged<List<SmartAppTileSpec>>? onDynamicAppsChanged;

  /// Host capability used by dictation composers to transcribe captured audio into a draft.
  final VoiceTranscriber? transcribe;

  /// Host capability used to open full-duplex voice mode on a conversation.
  final VoiceTalker? talk;

  /// Host capability used by read-aloud controls to synthesize assistant replies.
  final VoiceSpeaker? speak;

  @override
  State<LiveDesktopShell> createState() => _LiveDesktopShellState();
}

class _LiveDesktopShellState extends State<LiveDesktopShell> {
  Map<String, dynamic>? _data;
  Object? _error;
  StreamSubscription<void>? _watchSub;
  Timer? _resubscribeTimer;
  bool _loading = false;
  bool _reloadAgain = false;

  final Map<String, DesktopWindowState> _windowStates = {};
  final List<String> _zOrder = [];
  final List<String> _clientOpenedApps = [];
  final Set<String> _clientClosedWindowIds = {};
  final Set<String> _clientOpenedWidgets = {};
  final Set<String> _clientClosedWidgets = {};
  final PendingOpenWindowIntentTracker _intentTracker =
      PendingOpenWindowIntentTracker();
  Set<String> _dynamicAppIds = const {};
  String _lastDynamicSignature = '';
  bool _showDesktopOnly = false;

  DesktopPopoverKind _popover = DesktopPopoverKind.none;
  double _popoverAnchorX = 0;
  String? _dockMenuAppId;
  String? _barTooltipLabel;
  double _barTooltipAnchorX = 0;
  int _notificationsCount = 0;
  List<DesktopNotificationEntry> _notifications = const [];
  bool _notificationsLoaded = false;
  bool _notificationsLoading = false;
  StreamSubscription<NeuronSignal>? _notificationsSub;
  bool _smart = false;
  PresenceState? _presenceState;
  final StreamController<Presence> _presence =
      StreamController<Presence>.broadcast();
  StreamSubscription<void>? _settingsSub, _presenceSub;
  Timer? _presenceReopen;
  final Set<String> _pinnedAppIds = {};
  bool _pinsInitialized = false;
  Map<String, SmartAppTileSpec> _catalogById = const {};
  bool _allAppsOpen = false;
  bool _themeStudioOpen = false;
  bool _switcherOpen = false;
  int _switcherIndex = 0;
  DesktopSystemSheetKind _systemSheet = DesktopSystemSheetKind.none;

  @override
  void initState() {
    super.initState();
    HardwareKeyboard.instance.addHandler(_onHardwareKey);
    _reload();
    _subscribe();
    _watchSettings();
    _subscribeNotifications();
    unawaited(_loadNotifications());
  }

  IntoChatOSUiClient? get _notificationsClient =>
      widget.notificationsClient ?? widget.entries?.call(widget.brain).client;

  NeuronSignalWatcher? get _watchSignal {
    if (widget.watchSignal != null) return widget.watchSignal;
    final client = _notificationsClient;
    if (client == null) return null;
    return (contract, key, signal) => client
        .watches(widget.brain)
        .watch(
          WatchSourceSpec(
            contract,
            key.startsWith('${widget.brain}/') ? key : '${widget.brain}/$key',
            [signal],
          ),
        )
        .map((s) => s.payload);
  }

  void _subscribeNotifications() {
    _notificationsSub?.cancel();
    _notificationsSub = null;
    final client = _notificationsClient;
    if (client == null || !client.grpcLive) return;
    try {
      _notificationsSub = client
          .watches(widget.brain)
          .watch(
            const WatchSourceSpec(
              'IntoChat.OS.Modules.Notifications.INotificationsInbox',
              'inbox',
              ['NotificationSent'],
            ),
          )
          .listen(
            (_) => unawaited(_loadNotifications()),
            onError: (Object _) {},
          );
    } catch (_) {
      // Live watch is optional; opening the popover refreshes over HTTP.
    }
  }

  Future<void> _loadNotifications() async {
    final client = _notificationsClient;
    if (client == null) return;
    if (!_notificationsLoaded && mounted) {
      setState(() => _notificationsLoading = true);
    }
    try {
      final result = await client.listNotifications(widget.brain);
      if (!mounted) return;
      setState(() {
        _notifications = [
          for (final item in result.items)
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
        _notificationsCount = result.unreadCount;
        _notificationsLoaded = true;
        _notificationsLoading = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() => _notificationsLoading = false);
    }
  }

  void _markNotificationRead(String id) {
    final client = _notificationsClient;
    setState(() {
      var decremented = false;
      _notifications = [
        for (final item in _notifications)
          if (item.id == id && !item.read)
            (() {
              decremented = true;
              return DesktopNotificationEntry(
                id: item.id,
                sourceApp: item.sourceApp,
                title: item.title,
                body: item.body,
                priority: item.priority,
                read: true,
                createdUtc: item.createdUtc,
                actionUrl: item.actionUrl,
              );
            })()
          else
            item,
      ];
      if (decremented && _notificationsCount > 0) {
        _notificationsCount--;
      }
    });
    if (client == null) return;
    unawaited(
      client
          .markNotificationRead(widget.brain, id)
          .then((_) => _loadNotifications())
          .catchError((Object _) {}),
    );
  }

  void _markAllNotificationsRead() {
    final client = _notificationsClient;
    setState(() {
      _notifications = [
        for (final item in _notifications)
          if (item.read)
            item
          else
            DesktopNotificationEntry(
              id: item.id,
              sourceApp: item.sourceApp,
              title: item.title,
              body: item.body,
              priority: item.priority,
              read: true,
              createdUtc: item.createdUtc,
              actionUrl: item.actionUrl,
            ),
      ];
      _notificationsCount = 0;
    });
    if (client == null) return;
    unawaited(
      client
          .markAllNotificationsRead(widget.brain)
          .then((_) => _loadNotifications())
          .catchError((Object _) {}),
    );
  }

  void _clearNotifications() {
    final client = _notificationsClient;
    setState(() {
      _notifications = const [];
      _notificationsCount = 0;
    });
    if (client == null) return;
    unawaited(
      client
          .clearNotifications(widget.brain)
          .then((_) => _loadNotifications())
          .catchError((Object _) {}),
    );
  }

  static const _settingsContract = 'IntoChat.OS.Modules.Apps.IUserSettings';
  static const _presenceContract = 'IntoChat.OS.Modules.Apps.IPresence';
  static const _approvalsContract =
      'IntoChat.OS.Modules.Apps.Approvals.IApprovals';

  void _watchSettings() {
    unawaited(_readSmart());
    _settingsSub = widget.watch
        ?.call('os.settings', 'settings')
        .listen((_) => unawaited(_readSmart()), onError: (Object _) {});
  }

  String? _syncedStylePreset;

  static IcStylePreset? _parseStylePreset(String? raw) =>
      switch (raw?.trim().toLowerCase()) {
        'fluent-acrylic' => IcStylePreset.fluentAcrylic,
        'spatial-glass' => IcStylePreset.spatialGlass,
        'cyber-synaptic' => IcStylePreset.cyberSynaptic,
        'warm-paper' || 'intochat' || 'paper' => IcStylePreset.warmPaper,
        'fluent-warm-mica' => IcStylePreset.fluentWarmMica,
        'linear-carbon' => IcStylePreset.linearCarbon,
        'nordic-tactical' => IcStylePreset.nordicTactical,
        _ => null,
      };

  /// Syncs `ino.smart` and `stylePreset` from `IUserSettings`.
  Future<void> _readSmart() async {
    var on = false;
    String? remotePreset;
    try {
      final settings = await widget.read?.call(
        _settingsContract,
        'settings',
        'Read',
      );
      final variables = settings?['variables'];
      on =
          variables is List &&
          variables.any(
            (v) =>
                v is Map &&
                v['key'] == 'ino.smart' &&
                '${v['value']}'.toLowerCase() == 'on',
          );
      final presetRaw = settings?['stylePreset'] as String?;
      if (presetRaw != null && presetRaw.trim().isNotEmpty) {
        remotePreset = presetRaw.trim().toLowerCase();
      }
    } catch (_) {
      on = false;
    }
    if (!mounted) return;
    if (remotePreset != null && remotePreset != _syncedStylePreset) {
      _syncedStylePreset = remotePreset;
      final parsed = _parseStylePreset(remotePreset);
      if (parsed != null && parsed != widget.stylePreset) {
        widget.onSelectPreset(parsed);
      }
    }
    if (on == _smart) return;
    setState(() {
      _smart = on;
      if (!on) {
        _presenceState = null;
        if (_notificationsClient == null) {
          _notificationsCount = 0;
        }
      }
    });
    if (on) {
      _watchPresence();
      unawaited(_readPresence());
    } else {
      _presenceSub?.cancel();
      _presenceSub = null;
      _presenceReopen?.cancel();
      _presenceReopen = null;
    }
  }

  void _watchPresence() {
    _presenceSub?.cancel();
    final watch = widget.watch;
    if (watch == null) return;
    _presenceSub = watch('ino.presence', 'ino/presence').listen(
      (_) => unawaited(_readPresence()),
      onError: (Object _) => _presenceDropped(),
      onDone: _presenceDropped,
    );
  }

  void _presenceDropped() {
    if (!mounted || !_smart) return;
    _presence.add(
      const Presence(state: PresenceState.disconnected, revision: -1),
    );
    setState(() => _presenceState = PresenceState.disconnected);
    _presenceReopen ??= Timer(const Duration(seconds: 2), () {
      _presenceReopen = null;
      if (!mounted || !_smart) return;
      _watchPresence();
      unawaited(_readPresence());
    });
  }

  Future<void> _readPresence() async {
    try {
      final json = await widget.read?.call(
        _presenceContract,
        'ino/presence',
        'Read',
      );
      if (json == null || !mounted || !_smart) return;
      final presence = Presence.fromJson(json);
      _presence.add(presence);
      setState(() => _presenceState = presence.state);
    } catch (_) {
      // The next change signal reads again.
    }
    await _readApprovals();
  }

  Future<void> _readApprovals() async {
    var count = 0;
    try {
      final reply = await widget.read?.call(
        _approvalsContract,
        'approvals/index',
        'Pending',
      );
      final pending = reply?['value'];
      count = pending is List ? pending.length : 0;
    } catch (_) {
      count = 0;
    }
    if (mounted && _smart && _notificationsClient == null) {
      setState(() => _notificationsCount = count);
    }
  }

  @override
  void didUpdateWidget(LiveDesktopShell oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.name != widget.name || oldWidget.brain != widget.brain) {
      _data = null;
      _error = null;
      _windowStates.clear();
      _zOrder.clear();
      _clientOpenedApps.clear();
      _clientClosedWindowIds.clear();
      _clientOpenedWidgets.clear();
      _clientClosedWidgets.clear();
      _notifications = const [];
      _notificationsLoaded = false;
      _notificationsLoading = false;
      _reload();
      _subscribe();
      _subscribeNotifications();
      unawaited(_loadNotifications());
    } else if (oldWidget.notificationsClient != widget.notificationsClient ||
        oldWidget.entries != widget.entries) {
      _subscribeNotifications();
      unawaited(_loadNotifications());
    }
  }

  @override
  void dispose() {
    HardwareKeyboard.instance.removeHandler(_onHardwareKey);
    _watchSub?.cancel();
    _resubscribeTimer?.cancel();
    _settingsSub?.cancel();
    _presenceSub?.cancel();
    _presenceReopen?.cancel();
    _notificationsSub?.cancel();
    unawaited(_presence.close());
    for (final w in _windowStates.values) {
      w.dispose();
    }
    super.dispose();
  }

  /// Ensures pinned windows (`isPinned == true`) always stay above unpinned
  /// windows in `_zOrder` (always-on-top) with no duplicate IDs.
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

  List<_ParsedDesktopWindow> _currentMruWindows() {
    final view = _desktopView;
    if (view == null) return const [];
    final windows = _parseWindows(view.root);
    final byId = {for (final w in windows) w.windowId: w};
    final ordered = _zOrder
        .map((id) => byId[id])
        .whereType<_ParsedDesktopWindow>()
        .toList();
    return ordered.reversed.toList();
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
        if (mru.isNotEmpty) {
          final target = mru[_switcherIndex.clamp(0, mru.length - 1)];
          setState(() => _switcherOpen = false);
          _focusWindow(target);
        } else {
          setState(() => _switcherOpen = false);
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
      if (_systemSheet != DesktopSystemSheetKind.none) {
        setState(() => _systemSheet = DesktopSystemSheetKind.none);
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
      unawaited(_loadNotifications());
    }
  }

  void _subscribe() {
    _watchSub?.cancel();
    final watch = widget.watch;
    if (watch == null) return;
    _watchSub = watch('view', widget.name).listen(
      (_) => _reload(),
      onError: (Object _) => _scheduleReopen(),
      onDone: _scheduleReopen,
    );
  }

  void _scheduleReopen() {
    if (!mounted || _resubscribeTimer != null) return;
    _resubscribeTimer = Timer(const Duration(seconds: 2), () {
      _resubscribeTimer = null;
      if (!mounted) return;
      _reload();
      _subscribe();
    });
  }

  Future<void> _reload() async {
    if (_loading) {
      _reloadAgain = true;
      return;
    }
    _loading = true;
    try {
      final data = await widget.load('view', widget.name);
      if (mounted) {
        setState(() {
          _data = data;
          _error = null;
        });
      }
    } catch (e) {
      if (mounted && _data == null) {
        setState(() => _error = e);
      }
    } finally {
      _loading = false;
      if (mounted && _reloadAgain) {
        _reloadAgain = false;
        unawaited(_reload());
      }
    }
  }

  int get _generation => (_data?['generation'] as num?)?.toInt() ?? 0;

  // The keys of the desktop view as last read; only these activate on the view.
  Set<String> _viewKeys = const {};

  void _dispatchViewNode(Map<String, dynamic>? node) {
    final event = desktopViewEvent(node, _viewKeys, widget.name, _generation);
    if (event == null) return;
    unawaited(
      widget.onAction(event).then((_) => _reload()).catchError(_refused),
    );
  }

  // A refused activation (a stale generation, a changed view): read the view again and say so.
  void _refused(Object error) {
    unawaited(_reload());
    if (mounted) {
      showKitToast(
        context,
        'The desktop changed. Try again.',
        tone: UiTone.error,
      );
    }
  }

  bool _knowsDesktopApp(String app) {
    final norm = app.trim().toLowerCase();
    if (norm.isEmpty) return false;
    return widget.knowsApp(app) ||
        widget.knowsApp(norm) ||
        _dynamicAppIds.contains(norm);
  }

  Map<String, String> _collectDynamicApps(
    List<_ParsedDesktopWindow> windows,
    Map<String, Map<String, dynamic>> dockButtons,
  ) {
    final discovered = <String, String>{};
    for (final entry in dockButtons.entries) {
      final btn = entry.value;
      final rawArg = (btn['argument'] as String?)?.trim();
      final rawId = (rawArg != null && rawArg.isNotEmpty)
          ? rawArg.toLowerCase()
          : entry.key.substring(5).trim().toLowerCase();
      if (rawId.isEmpty) continue;
      final label = (btn['text'] as String?)?.trim();
      discovered[rawId] = (label != null && label.isNotEmpty)
          ? label
          : IcAppIcon.humanizeAppTitle(rawId);
    }
    for (final w in windows) {
      final rawId = w.appId.trim().toLowerCase();
      if (rawId.isEmpty) continue;
      discovered.putIfAbsent(
        rawId,
        () => w.title.trim().isNotEmpty
            ? w.title.trim()
            : IcAppIcon.humanizeAppTitle(rawId),
      );
    }
    for (final id in [
      ..._clientOpenedApps,
      ..._pinnedAppIds,
      ...widget.installedApps,
    ]) {
      final clean = id.trim().toLowerCase();
      if (clean.isEmpty) continue;
      discovered.putIfAbsent(clean, () => IcAppIcon.humanizeAppTitle(clean));
    }
    return discovered;
  }

  void _shellGesture(String app, String operation, String input) {
    final request = widget.requestApp;
    final scope = app == 'desktop' || app == 'settings' ? app : 'window';
    if (request == null || !desktopShellOperationAllowed(scope, operation)) {
      return;
    }
    if (scope == 'window' && !_knowsDesktopApp(app)) return;
    unawaited(
      request(
        app,
        operation,
        input,
      ).then((_) => _reload()).catchError(_refused),
    );
  }

  // A desktop button as the window chrome reads it: its key, label, argument and state.
  // A view stored before buttons carried commands has the argument in its emit.
  Map<String, dynamic> _button(ButtonNode node) =>
      desktopButton(node, legacyEmitArgs(_data?['view']));

  static Iterable<Node> _children(Node node) => switch (node) {
    ColumnNode(:final children) ||
    RowNode(:final children) ||
    GridNode(:final children) ||
    CardNode(:final children) ||
    SurfaceNode(:final children) => children,
    _ => const <Node>[],
  };

  static Iterable<Node> _walk(Node node) sync* {
    yield node;
    for (final child in _children(node)) {
      yield* _walk(child);
    }
  }

  // The desktop's published view: {generation, view}.
  AppView? get _desktopView => switch (_data?['view']) {
    final Map view => AppView.fromJson(Map<String, dynamic>.from(view)),
    _ => null,
  };

  // The host's window shell has a dock of `dock-{app}` tiles or legacy buttons.
  static bool _isDesktopTree(AppView? view) =>
      view != null &&
      _walk(view.root).any(
        (node) =>
            (node is AppTileNode && node.key.startsWith('dock-')) ||
            (node is ButtonNode && node.key.startsWith('dock-')),
      );

  String _titleForAppId(String appId) {
    final normalized = appId.trim().toLowerCase();
    if (_catalogById[normalized] case final spec? when spec.name.isNotEmpty) {
      return spec.name;
    }
    return IcAppIcon.humanizeAppTitle(appId);
  }

  String _inferAppId(String title, Map<String, dynamic>? pinNode) {
    if (pinNode?['argument'] case final String app when app.isNotEmpty) {
      return app;
    }
    final lower = title.trim().toLowerCase();
    for (final spec in _catalogById.values) {
      if (lower == spec.id.toLowerCase() || lower == spec.name.toLowerCase()) {
        return spec.id;
      }
    }
    final normalized = IcAppIcon.normalizeAppId(title);
    if (_catalogById.containsKey(normalized) ||
        IcAppIcon.builtInSpecs.containsKey(normalized)) {
      return normalized;
    }
    return lower.replaceAll(RegExp(r'[^a-z0-9]'), '');
  }

  List<_ParsedDesktopWindow> _parseWindows(Node root) {
    NeuronRefNode? viewportOf(Node node) => _children(node)
        .whereType<NeuronRefNode>()
        .where(
          (ref) =>
              ref.refKind == 'viewport' &&
              ref.neuronUri.contains('/apps/desktop/window/'),
        )
        .firstOrNull;
    final frameNodes = [
      for (final node in _walk(root))
        if (node is ColumnNode && viewportOf(node) != null) node,
    ];

    final serverWindowKeys = <String>{};
    final serverAppIds = <String>{};
    final result = <_ParsedDesktopWindow>[];
    for (var i = 0; i < frameNodes.length; i++) {
      final frame = frameNodes[i];
      final reference = viewportOf(frame)!;
      final winId = reference.neuronUri.substring(
        reference.neuronUri.lastIndexOf('/') + 1,
      );

      final numericId =
          int.tryParse(winId.replaceAll(RegExp(r'[^0-9]'), '')) ?? (i + 1);

      Map<String, dynamic>? focusNode;
      Map<String, dynamic>? newTabNode;
      Map<String, dynamic>? starNode;
      Map<String, dynamic>? pinNode;
      Map<String, dynamic>? closeNode;
      var title = winId;

      final chrome = frame.children.whereType<RowNode>().firstOrNull;
      for (final btn
          in chrome?.children.whereType<ButtonNode>() ?? const <ButtonNode>[]) {
        final k = btn.key;
        final map = _button(btn);
        if (k.startsWith('focus-')) {
          focusNode = map;
          title = btn.label;
        } else if (k.startsWith('newtab-')) {
          newTabNode = map;
        } else if (k.startsWith('star-')) {
          starNode = map;
        } else if (k.startsWith('pin-') || k.startsWith('unpin-')) {
          pinNode = map;
        } else if (k.startsWith('close-')) {
          closeNode = map;
        }
      }

      final tabsNode = frame.children.whereType<TabsNode>().firstOrNull;
      final tabs = <Map<String, dynamic>>[
        for (final item in tabsNode?.items ?? const <TabItem>[])
          {
            'key': item.key,
            'text': item.label,
            'selected': item.id == tabsNode!.selectedId,
            'enabled': true,
          },
      ];

      Map<String, dynamic>? viewportRef = {
        'kind': reference.refKind,
        'name': reference.neuronUri,
      };

      final appId = _inferAppId(title, pinNode);
      final winKey = '$winId:$appId';
      serverWindowKeys.add(winKey);
      serverAppIds.add(appId);
      if (_clientClosedWindowIds.contains(winKey) ||
          _clientClosedWindowIds.contains(winId)) {
        continue;
      }

      if (IcAppIcon.isArtCraftApp(appId)) {
        viewportRef = <String, dynamic>{
          'kind': 'view',
          'name': '${widget.brain}/apps/$appId/view',
        };
      } else if (appId == 'files' &&
          (viewportRef['name'] as String? ?? '').endsWith('/apps/files/main')) {
        viewportRef = <String, dynamic>{
          'kind': 'view',
          'name': '${widget.brain}/apps/files/view',
        };
      }
      result.add(
        _ParsedDesktopWindow(
          windowId: winId,
          numericId: numericId,
          appId: appId,
          title: title,
          neuronUri: 'neuron://${widget.brain}/apps/$appId',
          viewportRef: viewportRef,
          tabs: tabs,
          focusNode: focusNode,
          newTabNode: newTabNode,
          starNode: starNode,
          pinNode: pinNode,
          closeNode: closeNode,
        ),
      );
    }

    _clientClosedWindowIds.removeWhere(
      (k) => !k.startsWith('local-') && !serverWindowKeys.contains(k),
    );
    _clientOpenedApps.removeWhere(serverAppIds.contains);

    for (var i = 0; i < _clientOpenedApps.length; i++) {
      final appId = _clientOpenedApps[i];
      if (result.any((w) => w.appId == appId)) continue;
      final localWinId = 'local-$appId';
      if (_clientClosedWindowIds.contains('$localWinId:$appId') ||
          _clientClosedWindowIds.contains(localWinId)) {
        continue;
      }
      result.add(
        _ParsedDesktopWindow(
          windowId: localWinId,
          numericId: 100 + i,
          appId: appId,
          title: _titleForAppId(appId),
          neuronUri: 'neuron://${widget.brain}/apps/$appId',
          viewportRef: <String, dynamic>{
            'kind': 'view',
            'name': '${widget.brain}/apps/$appId/view',
          },
          tabs: const [],
        ),
      );
    }

    return result;
  }

  List<String> _parseOpenWidgets(Node root) {
    String? rawText;
    TextNode? previous;
    for (final node in _walk(root)) {
      if (node is! TextNode) continue;
      if (previous?.value == 'Usage') {
        rawText = node.value;
        break;
      }
      previous = node;
    }
    final serverWidgets = <String>{
      if (rawText != null && rawText.trim().isNotEmpty)
        for (final part in rawText.split(','))
          if (part.trim().isNotEmpty) part.trim().toLowerCase(),
    };
    _clientOpenedWidgets.removeWhere(serverWidgets.contains);
    _clientClosedWidgets.removeWhere((id) => !serverWidgets.contains(id));

    return [
      for (final id in SmartDesktopState.cardOrder)
        if ((serverWidgets.contains(id) || _clientOpenedWidgets.contains(id)) &&
            !_clientClosedWidgets.contains(id))
          id,
    ];
  }

  Map<String, dynamic> _appTileButton(AppTileNode node) => {
    'key': node.key,
    'text': node.title,
    'enabled': true,
    'argument': node.appId,
  };

  Map<String, Map<String, dynamic>> _parseDockButtons(Node root) => {
    for (final node in _walk(root))
      if (node is AppTileNode && node.key.startsWith('dock-'))
        node.key: _appTileButton(node)
      else if (node is ButtonNode && node.key.startsWith('dock-'))
        node.key: _button(node),
  };

  List<SmartAppTileSpec> _parseDockTiles(Node root) => [
    for (final node in _walk(root))
      if (node is AppTileNode && node.key.startsWith('dock-'))
        SmartAppTileSpec.fromNode(node, brain: widget.brain),
  ];

  List<SmartAppTileSpec> _parseCatalogTiles(Node root) => [
    for (final node in _walk(root))
      if (node is AppTileNode && node.key.startsWith('catalog-'))
        SmartAppTileSpec.fromNode(node, brain: widget.brain),
  ];

  Rect _initialWindowBounds(int index, Size desk) {
    final w = (desk.width * 0.64).clamp(680.0, 980.0);
    final h = (desk.height * 0.78).clamp(460.0, 680.0);
    // Position first window slightly right of center so SmartDesktop left cards remain visible.
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

  bool _shouldLaunchMaximized(String appId) {
    final normalized = appId.trim().toLowerCase();
    return _catalogById[normalized]?.launch == AppLaunch.maximized;
  }

  void _syncWindowStates(List<_ParsedDesktopWindow> parsed, Size desk) {
    for (var i = 0; i < parsed.length; i++) {
      final spec = parsed[i];
      final existing = _windowStates[spec.windowId];
      final nodePinned = (spec.pinNode?['key'] as String? ?? '').startsWith(
        'unpin-',
      );
      if (existing == null) {
        final optimisticId = 'local-${spec.appId}';
        final optimistic = spec.windowId == optimisticId
            ? null
            : _windowStates[optimisticId];
        final launchMax = _shouldLaunchMaximized(spec.appId);
        _windowStates[spec.windowId] = DesktopWindowState(
          id: spec.numericId,
          appId: spec.appId,
          title: spec.title,
          neuronUri: spec.neuronUri,
          bounds: optimistic?.bounds ?? _initialWindowBounds(i, desk),
          zoomed: optimistic?.zoomed ?? launchMax,
          minimized: optimistic?.minimized ?? false,
          isPinned: (optimistic?.isPinned ?? false) || nodePinned,
        );
        final optIdx = _zOrder.indexOf(optimisticId);
        if (optIdx >= 0 && !_zOrder.contains(spec.windowId)) {
          _zOrder[optIdx] = spec.windowId;
        } else if (!_zOrder.contains(spec.windowId)) {
          _zOrder.add(spec.windowId);
        }
      } else {
        existing.title = spec.title;
        if (!_zOrder.contains(spec.windowId)) {
          _zOrder.add(spec.windowId);
        }
      }
    }

    final activeIds = {for (final w in parsed) w.windowId};
    _windowStates.removeWhere((id, state) {
      final gone = !activeIds.contains(id);
      if (gone) state.dispose();
      return gone;
    });
    _zOrder.removeWhere((id) => !activeIds.contains(id));
    _normalizeZOrder();
  }

  void _focusWindow(_ParsedDesktopWindow spec) {
    final state = _windowStates[spec.windowId];
    if (state != null && state.minimized) {
      state.setMinimized(false);
    }
    setState(() {
      _showDesktopOnly = false;
      _zOrder.remove(spec.windowId);
      _zOrder.add(spec.windowId);
      _normalizeZOrder();
    });
    _dispatchViewNode(spec.focusNode);
  }

  void _closeWindow(_ParsedDesktopWindow spec) {
    setState(() {
      _clientOpenedApps.remove(spec.appId);
      _clientClosedWindowIds.add('${spec.windowId}:${spec.appId}');
    });
    if (spec.closeNode case final close?) {
      _dispatchViewNode(close);
    } else {
      _shellGesture('desktop', 'close-app', spec.appId);
    }
  }

  /// Auto-arranges all unpinned windows into a clean grid while strictly
  /// locking the `Rect` bounds of any pinned window (`isPinned == true`).
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
        final x = pad + col * (cellW + gap);
        final y = pad + row * (cellH + gap);
        unpinnedStates[i].setBounds(Rect.fromLTWH(x, y, cellW, cellH));
      }
      _normalizeZOrder();
    });
  }

  void _navigateSettingsSection(String? settingsSection) {
    if (settingsSection == null || settingsSection.isEmpty) return;
    _shellGesture('settings', 'navigate', settingsSection);
  }

  void _handleChildActivation(
    Map<String, dynamic> activation,
    List<_ParsedDesktopWindow> windows,
    Map<String, Map<String, dynamic>> dockButtons, {
    String? sourceAppId,
  }) {
    if (activation['kind'] == 'os_uri') {
      final uriStr = '${activation['uri'] ?? ''}';
      final sheet = DesktopSystemModalSheet.parseUri(uriStr);
      if (sheet != DesktopSystemSheetKind.none) {
        setState(() => _systemSheet = sheet);
        return;
      }
      final fallbackFrom = sourceAppId != null && sourceAppId.isNotEmpty
          ? '${widget.brain}/apps/$sourceAppId'
          : null;
      final route = _intentTracker.consumeUri(
        uriStr,
        fallbackFromNeuron: fallbackFrom,
      );
      if (route != null) {
        _launchOrFocusApp(
          route.app,
          windows,
          dockButtons,
          settingsSection: route.section,
          openInput: route.input,
          openRoute: route,
        );
        return;
      }
      final uri = Uri.tryParse(uriStr);
      final querySection = uri?.queryParameters['section'];
      if (uriStr.contains('settings')) {
        _launchOrFocusApp(
          'settings',
          windows,
          dockButtons,
          settingsSection: querySection,
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
        dockButtons,
        settingsSection: activation['tab'] as String?,
      );
    }
    widget.onActivate({'brainId': widget.brain, ...activation});
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
      sendAct: widget.onAction,
      targetNeuron: '${widget.brain}/apps/$appId',
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
          if (st.appId == appId) {
            st.reloadEpoch++;
          }
        }
      }
    });
  }

  void _openAppGraph(
    String appId,
    List<_ParsedDesktopWindow> windows,
    Map<String, Map<String, dynamic>> dockButtons,
  ) {
    // The Creator opens with the app's node selected in its brain graph.
    _launchOrFocusApp(
      'creator',
      windows,
      dockButtons,
      openInput: 'node:${widget.brain}/apps/$appId',
    );
  }

  void _launchOrFocusApp(
    String appId,
    List<_ParsedDesktopWindow> windows,
    Map<String, Map<String, dynamic>> dockButtons, {
    String? settingsSection,
    String? openInput,
    OpenWindowRoute? openRoute,
  }) {
    setState(() => _showDesktopOnly = false);
    if (appId.startsWith('os://')) {
      final parsedSheet = DesktopSystemModalSheet.parseUri(appId);
      if (parsedSheet != DesktopSystemSheetKind.none) {
        setState(() => _systemSheet = parsedSheet);
        return;
      }
    }
    if (appId == 'secure') {
      setState(() => _systemSheet = DesktopSystemSheetKind.secure);
      return;
    }
    if (appId == 'profile') {
      setState(() => _systemSheet = DesktopSystemSheetKind.profile);
      return;
    }
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
    if (appId == 'settings' && !shouldNavigateTo) {
      _navigateSettingsSection(settingsSection);
    }

    for (final w in windows.reversed) {
      if (w.appId == appId) {
        _focusWindow(w);
        if (shouldNavigateTo) {
          unawaited(
            _dispatchWindowNavigateTo(
              appId,
              windowId: w.windowId,
              input: effectiveInput,
              argsJson: argsJson,
              transition: transition,
              fromNeuron: fromNeuron,
            ),
          );
          widget.onActivate({
            'brainId': widget.brain,
            'kind': 'workspace',
            'application': appId,
            'operation': 'open',
            'input': ?effectiveInput,
            'argsJson': ?argsJson,
            'transition': transition,
            'fromNeuron': ?fromNeuron,
            'navigatedTo': true,
          });
          setState(() {
            _windowStates[w.windowId]?.reloadEpoch++;
          });
        }
        return;
      }
    }
    setState(() {
      final localWinId = 'local-$appId';
      _clientClosedWindowIds.removeWhere(
        (id) =>
            id == localWinId ||
            id == '$localWinId:$appId' ||
            id.endsWith(':$appId') ||
            _windowStates[id]?.appId == appId,
      );
      if (!_clientOpenedApps.contains(appId)) {
        _clientOpenedApps.add(appId);
      }
      _zOrder.remove(localWinId);
      _zOrder.add(localWinId);
      _normalizeZOrder();
    });
    if (dockButtons['dock-$appId'] case final dockBtn?) {
      _dispatchViewNode(dockBtn);
    } else {
      _shellGesture('desktop', 'open', appId);
    }
    if (appId == 'settings' && !shouldNavigateTo) {
      widget.onActivate({
        'kind': 'settings',
        'brainId': widget.brain,
        'tab': ?settingsSection,
      });
      return;
    }
    if (shouldNavigateTo) {
      unawaited(
        _dispatchWindowNavigateTo(
          appId,
          windowId: 'local-$appId',
          input: effectiveInput,
          argsJson: argsJson,
          transition: transition,
          fromNeuron: fromNeuron,
        ),
      );
    }
    widget.onActivate({
      'brainId': widget.brain,
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

  static const _debuggerContract =
      'IntoChat.OS.Modules.Apps.Debugger.IDebuggerReads';

  // Only the Creator's view gets the journal tail: its brain graph is the one place pulses are drawn. The tail starts at the
  // journal's head, read from the Creator's reads neuron, so nothing journaled before the window opened is replayed.
  Stream<List<JournalTailSignal>> Function()? _journalFor(String view) {
    final journal = widget.journal;
    final read = widget.read;
    if (journal == null ||
        read == null ||
        !view.startsWith('${widget.brain}/apps/creator/')) {
      return null;
    }
    return () async* {
      Object? head;
      try {
        head = (await read(
          _debuggerContract,
          'creator/reads',
          'Head',
        ))?['value'];
      } catch (_) {
        // No head, no pulses: the graph stays still rather than replaying the journal.
        return;
      }
      if (head is! num) return;
      yield* journal(widget.brain, head.toInt());
    };
  }

  static const double _kCompactBreakpoint = 600.0;
  static const double _kExpandedBreakpoint = 1024.0;

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

  Widget _adaptiveTopActions(
    List<_ParsedDesktopWindow> windows,
    Map<String, Map<String, dynamic>> dockButtons,
  ) => Positioned(
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
            _launchOrFocusApp('settings', windows, dockButtons);
          },
        ),
        if (widget.profileMenu != null)
          SizedBox(height: 32, child: widget.profileMenu),
      ],
    ),
  );

  int _adaptiveSelectedIndex(_ParsedDesktopWindow? topWindow) {
    if (_themeStudioOpen) return 3;
    if (_allAppsOpen) return 2;
    if (topWindow != null && topWindow.appId != 'ino') return 1;
    return 0;
  }

  Widget _buildSinglePaneContent({
    required _ParsedDesktopWindow? topWindow,
    required List<_ParsedDesktopWindow> windows,
    required Map<String, Map<String, dynamic>> dockButtons,
    required List<SmartAppTileSpec> dockApps,
    required List<String> openWidgets,
    required Size desk,
    required bool isCompact,
  }) {
    if (topWindow == null) {
      return SmartDesktop(
        hasOpenWindows: false,
        stylePreset: widget.stylePreset,
        barLayout: widget.barLayout,
        apps: dockApps,
        openCardIds: openWidgets,
        onOpenCard: (cardId) {
          setState(() {
            _clientClosedWidgets.remove(cardId);
            _clientOpenedWidgets.add(cardId);
          });
          _shellGesture('desktop', 'widget-open', cardId);
        },
        onCloseCard: (cardId) {
          setState(() {
            _clientOpenedWidgets.remove(cardId);
            _clientClosedWidgets.add(cardId);
          });
          _shellGesture('desktop', 'widget-close', cardId);
        },
        onLaunchApp: (appId) => _launchOrFocusApp(appId, windows, dockButtons),
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

    return ViewHost(
      key: ValueKey(
        'adaptive-viewport:${topWindow.viewportRef['name']}:${_windowStates[topWindow.windowId]?.reloadEpoch ?? 0}',
      ),
      kind: topWindow.viewportRef['kind'] as String,
      name: topWindow.viewportRef['name'] as String,
      load: widget.load,
      watch: widget.watch,
      watchSignal: _watchSignal,
      callNeuron: widget.callNeuron,
      onAction: (event) => _intentTracker.wrapAction(
        event,
        widget.onAction,
        fallbackFromNeuron: '${widget.brain}/apps/${topWindow.appId}',
      ),
      knowsApp: _knowsDesktopApp,
      transcribe: widget.transcribe,
      talk: widget.talk,
      speak: widget.speak,
      signals: _journalFor(topWindow.viewportRef['name'] as String),
      onActivate: (activation) => _handleChildActivation(
        activation,
        windows,
        dockButtons,
        sourceAppId: topWindow.appId,
      ),
    );
  }

  Future<void> _showActiveAppsSheet(
    BuildContext context,
    List<_ParsedDesktopWindow> orderedWindows,
    Map<String, _ParsedDesktopWindow> byId,
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
                          final target = byId[w.windowId];
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
    List<_ParsedDesktopWindow> windows,
    Map<String, Map<String, dynamic>> dockButtons,
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
                    _launchOrFocusApp(spec.id, windows, dockButtons);
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
    required IcColors c,
    required Size size,
    required Size desk,
    required List<_ParsedDesktopWindow> windows,
    required Map<String, _ParsedDesktopWindow> byId,
    required List<_ParsedDesktopWindow> orderedWindows,
    required List<_ParsedDesktopWindow> visibleWindows,
    required _ParsedDesktopWindow? topWindow,
    required Map<String, Map<String, dynamic>> dockButtons,
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
                        dockButtons: dockButtons,
                        dockApps: dockApps,
                        openWidgets: openWidgets,
                        desk: desk,
                        isCompact: true,
                      ),
                    ),
                    if (_systemSheet == DesktopSystemSheetKind.none &&
                        topWindow?.appId != 'ino')
                      Positioned(
                        right: 12,
                        bottom: 12,
                        child: InoOrbV2(
                          state: _presenceState ?? PresenceState.idle,
                          size: 48,
                          semanticsId: 'compact:ino-orb',
                          onTap: () {
                            setState(() {
                              _allAppsOpen = false;
                              _themeStudioOpen = false;
                            });
                            _launchOrFocusApp('ino', windows, dockButtons);
                          },
                        ),
                      ),
                    _adaptiveTopActions(windows, dockButtons),
                    if (_systemSheet != DesktopSystemSheetKind.none)
                      Positioned.fill(
                        child: DesktopSystemModalSheet(
                          kind: _systemSheet,
                          onClose: () => setState(
                            () => _systemSheet = DesktopSystemSheetKind.none,
                          ),
                          onOpenSheet: (next) =>
                              setState(() => _systemSheet = next),
                        ),
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
                      _launchOrFocusApp('ino', windows, dockButtons);
                    case 1:
                      unawaited(
                        _showActiveAppsSheet(compactCtx, orderedWindows, byId),
                      );
                    case 2:
                      unawaited(
                        _showAllAppsSheet(
                          compactCtx,
                          launcherApps,
                          windows,
                          dockButtons,
                        ),
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
    required IcColors c,
    required Size size,
    required Size desk,
    required List<_ParsedDesktopWindow> windows,
    required Map<String, _ParsedDesktopWindow> byId,
    required List<_ParsedDesktopWindow> orderedWindows,
    required List<_ParsedDesktopWindow> visibleWindows,
    required _ParsedDesktopWindow? topWindow,
    required Map<String, Map<String, dynamic>> dockButtons,
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
                  _launchOrFocusApp('ino', windows, dockButtons);
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
                    dockButtons: dockButtons,
                    dockApps: dockApps,
                    openWidgets: openWidgets,
                    desk: desk,
                    isCompact: false,
                  ),
                ),
                if (_smart)
                  Positioned.fill(
                    child: PresenceLayer(
                      presence: _presence.stream,
                      enabled: _smart,
                      onOpenIno: () =>
                          _launchOrFocusApp('ino', windows, dockButtons),
                      onAction: (action) {
                        if (action.kind == 'open' ||
                            action.kind == 'navigate') {
                          _launchOrFocusApp(
                            action.argument,
                            windows,
                            dockButtons,
                          );
                        }
                      },
                    ),
                  ),
                _adaptiveTopActions(windows, dockButtons),
                if (_systemSheet != DesktopSystemSheetKind.none)
                  Positioned.fill(
                    child: DesktopSystemModalSheet(
                      kind: _systemSheet,
                      onClose: () => setState(
                        () => _systemSheet = DesktopSystemSheetKind.none,
                      ),
                      onOpenSheet: (next) =>
                          setState(() => _systemSheet = next),
                    ),
                  ),
                if (_allAppsOpen)
                  Positioned.fill(
                    child: AllAppsLauncherOverlay(
                      apps: launcherApps,
                      onClose: () => setState(() => _allAppsOpen = false),
                      onLaunchApp: (appId) {
                        setState(() => _allAppsOpen = false);
                        _launchOrFocusApp(appId, windows, dockButtons);
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

  @override
  Widget build(BuildContext context) {
    final data = _data;
    final view = _desktopView;
    final isDesktopTree = _isDesktopTree(view);

    if (data == null || !isDesktopTree) {
      if (_error != null && data == null) {
        return Center(child: Text('$_error'));
      }
      return ViewHost(
        key: ValueKey('fallback:${widget.name}'),
        kind: 'view',
        name: widget.name,
        load: widget.load,
        watch: widget.watch,
        watchSignal: _watchSignal,
        callNeuron: widget.callNeuron,
        onAction: (event) => _intentTracker.wrapAction(event, widget.onAction),
        knowsApp: widget.knowsApp,
        transcribe: widget.transcribe,
        talk: widget.talk,
        speak: widget.speak,
        onActivate: widget.onActivate,
      );
    }

    final effectiveRoot = view!.root;
    _viewKeys = {
      for (final node in _walk(effectiveRoot))
        if (node is AppTileNode) node.key else if (node is ButtonNode) node.key,
      for (final node in _walk(effectiveRoot))
        if (node is TabsNode)
          for (final item in node.items) item.key,
    };
    final c = IcTheme.of(context);
    final dockTiles = _parseDockTiles(effectiveRoot);
    final catalogTiles = _parseCatalogTiles(effectiveRoot);
    final catalogById = <String, SmartAppTileSpec>{
      for (final tile in catalogTiles) tile.id.toLowerCase(): tile,
      for (final tile in dockTiles) tile.id.toLowerCase(): tile,
    };
    _catalogById = catalogById;

    final windows = _parseWindows(effectiveRoot);
    final dockButtons = _parseDockButtons(effectiveRoot);
    final openWidgets = _parseOpenWidgets(effectiveRoot);
    final discoveredApps = _collectDynamicApps(windows, dockButtons);

    if (!_pinsInitialized && (dockTiles.isNotEmpty || dockButtons.isNotEmpty)) {
      _pinsInitialized = true;
      for (final tile in dockTiles) {
        _pinnedAppIds.add(tile.id.toLowerCase());
      }
      for (final entry in dockButtons.entries) {
        final rawArg = (entry.value['argument'] as String?)?.trim();
        final rawId = (rawArg != null && rawArg.isNotEmpty)
            ? rawArg
            : entry.key.substring(5).trim();
        if (rawId.isNotEmpty) {
          _pinnedAppIds.add(rawId.toLowerCase());
        }
      }
    }

    final dockSeen = <String>{};
    final dockApps = <SmartAppTileSpec>[];
    for (final tile in dockTiles) {
      if (dockSeen.add(tile.id.toLowerCase())) {
        dockApps.add(tile);
      }
    }
    for (final entry in discoveredApps.entries) {
      if (dockSeen.add(entry.key)) {
        dockApps.add(
          catalogById[entry.key] ??
              SmartAppTileSpec.fromIconSpec(
                entry.key,
                titleOverride: entry.value,
                brain: widget.brain,
              ),
        );
      }
    }

    final launcherSeen = <String>{};
    final launcherApps = <SmartAppTileSpec>[];
    for (final tile in catalogTiles) {
      if (launcherSeen.add(tile.id.toLowerCase())) {
        launcherApps.add(tile);
      }
    }
    for (final tile in dockApps) {
      if (launcherSeen.add(tile.id.toLowerCase())) {
        launcherApps.add(tile);
      }
    }
    for (final entry in discoveredApps.entries) {
      if (launcherSeen.add(entry.key)) {
        launcherApps.add(
          SmartAppTileSpec.fromIconSpec(
            entry.key,
            titleOverride: entry.value,
            categoryOverride: 'Micro-Apps',
            brain: widget.brain,
          ),
        );
      }
    }
    _dynamicAppIds = {
      ...discoveredApps.keys,
      for (final tile in launcherApps) tile.id.toLowerCase(),
    };

    if (widget.onDynamicAppsChanged != null) {
      final sig = [
        for (final s in launcherApps) '${s.id}:${s.name}:${s.launch.name}',
      ].join('|');
      if (sig != _lastDynamicSignature) {
        _lastDynamicSignature = sig;
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) {
            widget.onDynamicAppsChanged?.call(launcherApps);
          }
        });
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
        _syncWindowStates(windows, desk);

        final byId = {for (final w in windows) w.windowId: w};
        final orderedWindows = _zOrder
            .map((id) => byId[id])
            .whereType<_ParsedDesktopWindow>()
            .toList();
        final visibleWindows = _showDesktopOnly
            ? const <_ParsedDesktopWindow>[]
            : [
                for (final w in orderedWindows)
                  if (!(_windowStates[w.windowId]?.minimized ?? false)) w,
              ];
        final topWindow = visibleWindows.isEmpty ? null : visibleWindows.last;
        final openAppIds = {for (final w in windows) w.appId};

        if (isCompact) {
          return _buildCompactShell(
            context: context,
            c: c,
            size: size,
            desk: desk,
            windows: windows,
            byId: byId,
            orderedWindows: orderedWindows,
            visibleWindows: visibleWindows,
            topWindow: topWindow,
            dockButtons: dockButtons,
            dockApps: dockApps,
            launcherApps: launcherApps,
            openWidgets: openWidgets,
          );
        }

        if (isMedium) {
          return _buildMediumShell(
            context: context,
            c: c,
            size: size,
            desk: desk,
            windows: windows,
            byId: byId,
            orderedWindows: orderedWindows,
            visibleWindows: visibleWindows,
            topWindow: topWindow,
            dockButtons: dockButtons,
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
              // 1. Ambient glass radial glows matching E:\intochat\design\apps\os\lib\shell\desktop.dart
              if (c.isGlass)
                Positioned.fill(
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
                              c.secondaryGlow.withValues(
                                alpha: c.isDark ? 0.12 : 0.06,
                              ),
                              const Color(0x00000000),
                            ],
                          ),
                        ),
                      ),
                    ),
                  ),
                ),

              // 2. SmartDesktop (Interactive 3D Orbital Neural Graph + Whiteboard Sticky Cards)
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
                  onOpenCard: (cardId) {
                    setState(() {
                      _clientClosedWidgets.remove(cardId);
                      _clientOpenedWidgets.add(cardId);
                    });
                    _shellGesture('desktop', 'widget-open', cardId);
                  },
                  onCloseCard: (cardId) {
                    setState(() {
                      _clientOpenedWidgets.remove(cardId);
                      _clientClosedWidgets.add(cardId);
                    });
                    _shellGesture('desktop', 'widget-close', cardId);
                  },
                  onLaunchApp: (appId) =>
                      _launchOrFocusApp(appId, windows, dockButtons),
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

              // 3. Free-Floating, Draggable, 8-Way Resizable Acrylic Windows
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
                        key: ValueKey('window:${spec.windowId}'),
                        window: _windowStates[spec.windowId]!,
                        focused: spec == topWindow,
                        forceZoom: focusMode,
                        desk: desk,
                        tabs: [
                          for (final t in spec.tabs)
                            DesktopTabSpec(
                              key: '${t['key']}',
                              label: '${t['text'] ?? ''}',
                              selected: t['selected'] == true,
                              onTap: () => _dispatchViewNode(t),
                            ),
                        ],
                        onFocus: () => _focusWindow(spec),
                        onClose: () => _closeWindow(spec),
                        onMinimize: () {
                          final st = _windowStates[spec.windowId];
                          if (st != null) {
                            setState(() => st.setMinimized(!st.minimized));
                          }
                        },
                        onNewTab: spec.newTabNode != null
                            ? () => _dispatchViewNode(spec.newTabNode)
                            : null,
                        onFavourite: spec.starNode != null
                            ? () => _dispatchViewNode(spec.starNode)
                            : null,
                        isFavourite: (spec.starNode?['text'] as String? ?? '')
                            .toLowerCase()
                            .contains('remove'),
                        onPin: () {
                          setState(() => _normalizeZOrder());
                          if (spec.pinNode != null) {
                            _dispatchViewNode(spec.pinNode);
                          }
                        },
                        isPinned:
                            _windowStates[spec.windowId]?.isPinned ?? false,
                        onReload: () {
                          setState(() {});
                          unawaited(_reload());
                        },
                        onHardRestartSandbox: () {
                          _shellGesture(spec.appId, 'sandbox-restart', '');
                        },
                        onSwitchExecutionMode: (mode) {
                          _shellGesture(spec.appId, 'switch-mode', mode);
                        },
                        onInspectSandbox: () {
                          _launchOrFocusApp('creator', windows, dockButtons);
                          widget.onActivate({
                            'brainId': widget.brain,
                            'kind': 'workspace',
                            'application': 'creator',
                            'operation': 'inspect',
                            'resource': 'app.${spec.appId}',
                          });
                        },
                        onOpenAppSettings: () =>
                            _openAppGraph(spec.appId, windows, dockButtons),
                        child: spec.appId == 'workspace'
                            ? SpatialAppCanvas(
                                windows: [
                                  for (final w in visibleWindows)
                                    if (w.windowId != spec.windowId) w.windowId,
                                ],
                                windowStates: _windowStates,
                                topWindowId: topWindow?.windowId,
                                onFocusWindow: (id) {
                                  final target = byId[id];
                                  if (target != null) _focusWindow(target);
                                },
                                onCloseWindow: (id) {
                                  final target = byId[id];
                                  if (target != null) _closeWindow(target);
                                },
                                onToggleZoom: (id) {
                                  _windowStates[id]?.toggleZoom();
                                },
                                onPinWindow: (id) {
                                  final target = byId[id];
                                  if (target != null) {
                                    setState(() => _normalizeZOrder());
                                    if (target.pinNode != null) {
                                      _dispatchViewNode(target.pinNode);
                                    }
                                  }
                                },
                                onReloadWindow: (id) {
                                  setState(() {});
                                  unawaited(_reload());
                                },
                                onHardRestartWindow: (id) {
                                  final target = byId[id];
                                  if (target != null) {
                                    _shellGesture(
                                      target.appId,
                                      'sandbox-restart',
                                      '',
                                    );
                                  }
                                },
                                onSwitchExecutionMode: (id, mode) {
                                  final target = byId[id];
                                  if (target != null) {
                                    _shellGesture(
                                      target.appId,
                                      'switch-mode',
                                      mode,
                                    );
                                  }
                                },
                                onInspectSandbox: (id) {
                                  final target = byId[id];
                                  if (target != null) {
                                    _launchOrFocusApp(
                                      'creator',
                                      windows,
                                      dockButtons,
                                    );
                                    widget.onActivate({
                                      'brainId': widget.brain,
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
                                    _openAppGraph(
                                      target.appId,
                                      windows,
                                      dockButtons,
                                    );
                                  }
                                },
                                tabs: {
                                  for (final w in visibleWindows)
                                    if (w.windowId != spec.windowId)
                                      w.windowId: [
                                        for (final t in w.tabs)
                                          DesktopTabSpec(
                                            key: '${t['key']}',
                                            label: '${t['text'] ?? ''}',
                                            selected: t['selected'] == true,
                                            onTap: () => _dispatchViewNode(t),
                                          ),
                                      ],
                                },
                                windowBuilders: {
                                  for (final w in visibleWindows)
                                    if (w.windowId != spec.windowId)
                                      w.windowId: (context) => ViewHost(
                                        key: ValueKey(
                                          'canvas-viewport:${w.viewportRef['name']}:${_windowStates[w.windowId]?.reloadEpoch ?? 0}',
                                        ),
                                        kind: w.viewportRef['kind'] as String,
                                        name: w.viewportRef['name'] as String,
                                        load: widget.load,
                                        watch: widget.watch,
                                        watchSignal: _watchSignal,
                                        callNeuron: widget.callNeuron,
                                        onAction: (event) =>
                                            _intentTracker.wrapAction(
                                              event,
                                              widget.onAction,
                                              fallbackFromNeuron:
                                                  '${widget.brain}/apps/${w.appId}',
                                            ),
                                        knowsApp: _knowsDesktopApp,
                                        transcribe: widget.transcribe,
                                        talk: widget.talk,
                                        speak: widget.speak,
                                        signals: _journalFor(
                                          w.viewportRef['name'] as String,
                                        ),
                                        onActivate: (activation) =>
                                            _handleChildActivation(
                                              activation,
                                              windows,
                                              dockButtons,
                                              sourceAppId: w.appId,
                                            ),
                                      ),
                                },
                              )
                            : ViewHost(
                                key: ValueKey(
                                  'viewport:${spec.viewportRef['name']}:${_windowStates[spec.windowId]?.reloadEpoch ?? 0}',
                                ),
                                kind: spec.viewportRef['kind'] as String,
                                name: spec.viewportRef['name'] as String,
                                load: widget.load,
                                watch: widget.watch,
                                watchSignal: _watchSignal,
                                callNeuron: widget.callNeuron,
                                onAction: (event) => _intentTracker.wrapAction(
                                  event,
                                  widget.onAction,
                                  fallbackFromNeuron:
                                      '${widget.brain}/apps/${spec.appId}',
                                ),
                                knowsApp: _knowsDesktopApp,
                                transcribe: widget.transcribe,
                                talk: widget.talk,
                                speak: widget.speak,
                                signals: _journalFor(
                                  spec.viewportRef['name'] as String,
                                ),
                                onActivate: (activation) =>
                                    _handleChildActivation(
                                      activation,
                                      windows,
                                      dockButtons,
                                      sourceAppId: spec.appId,
                                    ),
                              ),
                      ),
                  ],
                ),
              ),

              // 3b. Ino's presence: above the windows, below the bar and its popovers and sheets.
              if (_smart)
                Positioned(
                  left: 0,
                  top: 0,
                  width: desk.width,
                  height: desk.height,
                  child: PresenceLayer(
                    presence: _presence.stream,
                    enabled: _smart,
                    onOpenIno: () =>
                        _launchOrFocusApp('ino', windows, dockButtons),
                    onAction: (action) {
                      if (action.kind == 'open' || action.kind == 'navigate') {
                        _launchOrFocusApp(
                          action.argument,
                          windows,
                          dockButtons,
                        );
                      }
                    },
                  ),
                ),

              // 4. Adaptive 36px Glass OS Bar & Dock
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
                  notifications: _notificationsCount,
                  presence: _smart ? _presenceState : null,
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
                    _launchOrFocusApp(appId, windows, dockButtons);
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
                    _launchOrFocusApp('settings', windows, dockButtons);
                  },
                  onOpenCompute: () =>
                      _togglePopover(DesktopPopoverKind.tokens),
                  onOpenNotifications: () =>
                      _togglePopover(DesktopPopoverKind.notifications),
                  onOpenClock: () => _togglePopover(DesktopPopoverKind.clock),
                ),
              ),

              // 5. Unclipped Hover Tooltip Above OsBar
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

              // 6. OS Bar Popovers (Clock, Tokens & Compute, Notifications, Right-Click Dock Menu)
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
                  notificationsCount: _notificationsCount,
                  notifications: _notifications,
                  notificationsLoaded: _notificationsLoaded,
                  notificationsLoading: _notificationsLoading,
                  pinnedAppIds: _pinnedAppIds,
                  windows: [
                    for (final w in orderedWindows.reversed)
                      DesktopWindowSummary(
                        windowId: w.windowId,
                        numericId: w.numericId,
                        appId: w.appId,
                        title: w.title,
                        minimized:
                            _windowStates[w.windowId]?.minimized ?? false,
                        isPinned: _pinnedAppIds.contains(w.appId),
                      ),
                  ],
                  onClose: () =>
                      setState(() => _popover = DesktopPopoverKind.none),
                  onOpenSettings: () {
                    final targetApp = _dockMenuAppId;
                    setState(() => _popover = DesktopPopoverKind.none);
                    if (targetApp != null && targetApp.isNotEmpty) {
                      _openAppGraph(targetApp, windows, dockButtons);
                    } else {
                      _launchOrFocusApp('settings', windows, dockButtons);
                    }
                  },
                  onOpenComputeSettings: () {
                    if (widget.onOpenCompute != null) {
                      widget.onOpenCompute!();
                    } else {
                      _launchOrFocusApp(
                        'settings',
                        windows,
                        dockButtons,
                        settingsSection: 'billing',
                      );
                    }
                  },
                  onMarkNotificationRead: _markNotificationRead,
                  onMarkAllNotificationsRead: _markAllNotificationsRead,
                  onClearNotifications: _clearNotifications,
                  onFocusWindow: (winId) {
                    final target = byId[winId];
                    if (target != null) _focusWindow(target);
                  },
                  onNewWindowForApp: (appId) {
                    final dockBtn = dockButtons['dock-$appId'];
                    if (dockBtn != null) {
                      _dispatchViewNode(dockBtn);
                    } else {
                      _launchOrFocusApp(appId, windows, dockButtons);
                    }
                  },
                  onTogglePinApp: (appId) {
                    setState(() {
                      if (_pinnedAppIds.contains(appId)) {
                        _pinnedAppIds.remove(appId);
                      } else {
                        _pinnedAppIds.add(appId);
                      }
                    });
                    for (final w in windows) {
                      if (w.appId == appId && w.pinNode != null) {
                        _dispatchViewNode(w.pinNode);
                        break;
                      }
                    }
                  },
                  onQuitApp: (appId) {
                    for (final w in windows) {
                      if (w.appId == appId) {
                        _closeWindow(w);
                      }
                    }
                  },
                ),
              ],

              // 7. Alt+` (⌥`) Visual MRU Window Switcher
              if (_switcherOpen && orderedWindows.isNotEmpty)
                Positioned.fill(
                  child: AltBacktickWindowSwitcher(
                    windows: [
                      for (final w in orderedWindows.reversed)
                        DesktopWindowSummary(
                          windowId: w.windowId,
                          numericId: w.numericId,
                          appId: w.appId,
                          title: w.title,
                          minimized:
                              _windowStates[w.windowId]?.minimized ?? false,
                          isPinned: _pinnedAppIds.contains(w.appId),
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

              // 8. Declarative os:// System Modal Sheets & Utility Windows (Secure, Profile, Install, Re-Consent, Conflict, 2FA)
              if (_systemSheet != DesktopSystemSheetKind.none)
                Positioned.fill(
                  child: DesktopSystemModalSheet(
                    kind: _systemSheet,
                    onClose: () => setState(
                      () => _systemSheet = DesktopSystemSheetKind.none,
                    ),
                    onOpenSheet: (next) => setState(() => _systemSheet = next),
                  ),
                ),

              // 9. All Apps & ArtCraft Studio Catalog Launcher Overlay
              if (_allAppsOpen)
                Positioned.fill(
                  child: AllAppsLauncherOverlay(
                    apps: launcherApps,
                    onClose: () => setState(() => _allAppsOpen = false),
                    onLaunchApp: (appId) {
                      setState(() => _allAppsOpen = false);
                      _launchOrFocusApp(appId, windows, dockButtons);
                    },
                  ),
                ),

              // 10. Floating Live Theme Studio Overlay (anchored bottom-right above the OS bar)
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
            ],
          ),
        );
      },
    );
  }
}

/// The view event a desktop node sends: only a node the published view holds activates, only by its key; any
/// other data on the node (an old `action`) is never sent anywhere.
Map<String, dynamic>? desktopViewEvent(
  Map<String, dynamic>? node,
  Set<String> viewKeys,
  String name,
  int generation,
) {
  if (node == null || node['enabled'] == false) return null;
  final key = node['key'] as String?;
  if (key == null || !viewKeys.contains(key)) return null;
  return {'kind': 'view', 'name': name, 'generation': generation, 'key': key};
}

// The shell's own desktop gestures that no view node carries. Task 22 debt: each becomes a node of the desktop
// view. Only these (app, operation) pairs are sent, with arguments the shell itself holds; anything else is refused.
// `window` stands for the app of a window the shell shows (sandbox controls).
const _desktopShellOperations = <(String, String)>{
  ('desktop', 'open'),
  ('desktop', 'close-app'),
  ('desktop', 'widget-open'),
  ('desktop', 'widget-close'),
  ('settings', 'navigate'),
  ('window', 'sandbox-restart'),
  ('window', 'switch-mode'),
};

bool desktopShellOperationAllowed(String app, String operation) =>
    _desktopShellOperations.contains((app, operation));

/// A desktop button as the window chrome reads it: its key, label, argument
/// (the command's text args, else the legacy emit's from [legacyArgs]) and state.
Map<String, dynamic> desktopButton(
  ButtonNode node,
  Map<String, Object?> legacyArgs,
) => {
  'key': node.key,
  'text': node.label,
  'enabled': node.enabled,
  'argument': switch (node.command?.args ?? legacyArgs[node.key]) {
    final String text => text,
    _ => null,
  },
};

/// The `emit.args` of every keyed button in raw view JSON written before
/// buttons carried commands, by key.
Map<String, Object?> legacyEmitArgs(Object? view) {
  final found = <String, Object?>{};
  void walk(Object? node) {
    if (node is List) {
      for (final item in node) {
        walk(item);
      }
    } else if (node is Map) {
      final key = node['key'];
      final emit = node['emit'];
      if (key is String && emit is Map && node['command'] == null) {
        found[key] = emit['args'];
      }
      for (final value in node.values) {
        walk(value);
      }
    }
  }

  walk(view);
  return found;
}
