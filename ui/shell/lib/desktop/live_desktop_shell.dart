import 'dart:async';
import 'dart:math';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import '../workspace/spatial_app_canvas.dart';
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
    this.requestApp,
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
  bool _showDesktopOnly = false;

  DesktopPopoverKind _popover = DesktopPopoverKind.none;
  double _popoverAnchorX = 0;
  String? _dockMenuAppId;
  String? _barTooltipLabel;
  double _barTooltipAnchorX = 0;
  int _notificationsCount = 0;
  bool _smart = false;
  PresenceState? _presenceState;
  final StreamController<Presence> _presence =
      StreamController<Presence>.broadcast();
  StreamSubscription<void>? _settingsSub, _presenceSub;
  Timer? _presenceReopen;
  final Set<String> _pinnedAppIds = {
    'ino',
    'files',
    'photocraft',
    'filmcraft',
    'creator',
    'gallery',
    'friends',
    'workspace',
    'settings',
  };
  bool _allAppsOpen = false;
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

  /// Smart mode is the `ino.smart` user variable; off when it cannot be read.
  Future<void> _readSmart() async {
    var on = false;
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
    } catch (_) {
      on = false;
    }
    if (!mounted || on == _smart) return;
    setState(() {
      _smart = on;
      if (!on) {
        _presenceState = null;
        _notificationsCount = 0;
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
    if (mounted && _smart) setState(() => _notificationsCount = count);
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
      _reload();
      _subscribe();
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

  void _shellGesture(String app, String operation, String input) {
    final request = widget.requestApp;
    final scope = app == 'desktop' || app == 'settings' ? app : 'window';
    if (request == null || !desktopShellOperationAllowed(scope, operation)) {
      return;
    }
    if (scope == 'window' && !widget.knowsApp(app)) return;
    unawaited(
      request(
        app,
        operation,
        input,
      ).then((_) => _reload()).catchError(_refused),
    );
  }

  // A desktop button as the window chrome reads it: its key, label, argument and state.
  static Map<String, dynamic> _button(ButtonNode node) => {
    'key': node.key,
    'text': node.label,
    'enabled': node.enabled,
    'argument': switch (node.emit?.args) {
      final String text => text,
      _ => null,
    },
  };

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

  // The host's window shell has a dock of `dock-{app}` buttons.
  static bool _isDesktopTree(AppView? view) =>
      view != null &&
      _walk(view.root)
          .any((node) => node is ButtonNode && node.key.startsWith('dock-'));

  static String _titleForAppId(String appId) {
    for (final s in allSmartAppSpecs) {
      if (s.id == appId) return s.name;
    }
    final spec = IcAppIcon.resolveSpec(appId);
    if (spec.appId == appId && spec.title.isNotEmpty) return spec.title;
    return switch (appId) {
      'ino' => 'Ino',
      'files' => 'Files',
      'photocraft' => 'PhotoCraft',
      'filmcraft' => 'FilmCraft',
      'creator' => 'Creator',
      'gallery' => 'UI Gallery',
      'post' => 'Post',
      'workspace' => 'Workspace',
      'settings' => 'Settings',
      _ =>
        appId.isEmpty
            ? 'App'
            : '${appId[0].toUpperCase()}${appId.substring(1)}',
    };
  }

  static String _inferAppId(String title, Map<String, dynamic>? pinNode) {
    if (pinNode?['argument'] case final String app when app.isNotEmpty) {
      return app;
    }
    final normalized = IcAppIcon.normalizeAppId(title);
    if (IcAppIcon.builtInSpecs.containsKey(normalized)) {
      return normalized;
    }
    final lower = title.toLowerCase();
    for (final s in allSmartAppSpecs) {
      if (lower.contains(s.id) || lower.contains(s.name.toLowerCase())) {
        return s.id;
      }
    }
    if (lower.contains('ino')) return 'ino';
    if (lower.contains('photocraft')) return 'photocraft';
    if (lower.contains('filmcraft') || lower.contains('videocraft')) {
      return 'filmcraft';
    }
    if (lower.contains('gallery')) return 'gallery';
    if (lower.contains('creator')) return 'creator';
    if (lower.contains('file')) return 'files';
    if (lower.contains('post')) return 'post';
    if (lower.contains('workspace')) return 'workspace';
    if (lower.contains('setting')) return 'settings';
    if (lower.contains('circle')) return 'circleroom';
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

  Map<String, Map<String, dynamic>> _parseDockButtons(Node root) => {
    for (final node in _walk(root))
      if (node is ButtonNode && node.key.startsWith('dock-'))
        node.key: _button(node),
  };

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
        _windowStates[spec.windowId] = DesktopWindowState(
          id: spec.numericId,
          appId: spec.appId,
          title: spec.title,
          neuronUri: spec.neuronUri,
          bounds: optimistic?.bounds ?? _initialWindowBounds(i, desk),
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
    Map<String, Map<String, dynamic>> dockButtons,
  ) {
    if (activation['kind'] == 'os_uri') {
      final uriStr = '${activation['uri'] ?? ''}';
      final sheet = DesktopSystemModalSheet.parseUri(uriStr);
      if (sheet != DesktopSystemSheetKind.none) {
        setState(() => _systemSheet = sheet);
        return;
      }
      final uri = Uri.tryParse(uriStr);
      final queryApp = uri?.queryParameters['app'];
      final querySection = uri?.queryParameters['section'];
      if (queryApp != null && queryApp.isNotEmpty) {
        final queryInput = uri?.queryParameters['input'];
        _launchOrFocusApp(
          queryApp,
          windows,
          dockButtons,
          settingsSection: querySection,
          openInput: queryInput != null && queryInput.isNotEmpty
              ? queryInput
              : null,
        );
        return;
      }
      if (uriStr.contains('settings')) {
        _launchOrFocusApp(
          'settings',
          windows,
          dockButtons,
          settingsSection: querySection,
        );
        return;
      }
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
    if (appId == 'settings') {
      _navigateSettingsSection(settingsSection);
    }
    for (final w in windows.reversed) {
      if (w.appId == appId) {
        _focusWindow(w);
        if (openInput != null) {
          widget.onActivate({
            'brainId': widget.brain,
            'kind': 'workspace',
            'application': appId,
            'operation': 'open',
            'input': openInput,
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
    if (appId == 'settings') {
      widget.onActivate({
        'kind': 'settings',
        'brainId': widget.brain,
        'tab': ?settingsSection,
      });
      return;
    }
    widget.onActivate({
      'brainId': widget.brain,
      'kind': 'workspace',
      'application': appId,
      'operation': 'open',
      'input': ?openInput,
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
        onAction: widget.onAction,
        knowsApp: widget.knowsApp,
        onActivate: widget.onActivate,
      );
    }

    final effectiveRoot = view!.root;
    _viewKeys = {
      for (final node in _walk(effectiveRoot))
        if (node is ButtonNode) node.key,
      for (final node in _walk(effectiveRoot))
        if (node is TabsNode)
          for (final item in node.items) item.key,
    };
    final c = IcTheme.of(context);
    final windows = _parseWindows(effectiveRoot);
    final dockButtons = _parseDockButtons(effectiveRoot);
    final openWidgets = _parseOpenWidgets(effectiveRoot);

    return LayoutBuilder(
      builder: (context, constraints) {
        final size = constraints.biggest;
        final focusMode = size.width < 1024 || size.height < 640;
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
                                        onAction: widget.onAction,
                                        knowsApp: widget.knowsApp,
                                        signals: _journalFor(
                                          w.viewportRef['name'] as String,
                                        ),
                                        onActivate: (activation) =>
                                            _handleChildActivation(
                                              activation,
                                              windows,
                                              dockButtons,
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
                                onAction: widget.onAction,
                                knowsApp: widget.knowsApp,
                                signals: _journalFor(
                                  spec.viewportRef['name'] as String,
                                ),
                                onActivate: (activation) =>
                                    _handleChildActivation(
                                      activation,
                                      windows,
                                      dockButtons,
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
                  openAppIds: openAppIds,
                  focusedAppId: topWindow?.appId,
                  paletteOpen: widget.paletteOpen,
                  allAppsOpen: _allAppsOpen,
                  showingDesktop: _showDesktopOnly || visibleWindows.isEmpty,
                  notifications: _notificationsCount,
                  presence: _smart ? _presenceState : null,
                  profileMenu: widget.profileMenu,
                  onTogglePalette: () {
                    setState(() {
                      _popover = DesktopPopoverKind.none;
                      _allAppsOpen = false;
                    });
                    widget.onTogglePalette();
                  },
                  onToggleAllApps: () {
                    setState(() {
                      _popover = DesktopPopoverKind.none;
                      _allAppsOpen = !_allAppsOpen;
                    });
                  },
                  onToggleShowDesktop: () {
                    setState(() {
                      _popover = DesktopPopoverKind.none;
                      _allAppsOpen = false;
                      _showDesktopOnly = !_showDesktopOnly;
                    });
                  },
                  onCycleStylePreset: () {
                    final nextIdx =
                        (IcStylePreset.values.indexOf(widget.stylePreset) + 1) %
                        IcStylePreset.values.length;
                    widget.onSelectPreset(IcStylePreset.values[nextIdx]);
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
                  notificationsCount: _notificationsCount,
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
                  onClearNotifications: () =>
                      setState(() => _notificationsCount = 0),
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
                    onClose: () => setState(() => _allAppsOpen = false),
                    onLaunchApp: (appId) {
                      setState(() => _allAppsOpen = false);
                      _launchOrFocusApp(appId, windows, dockButtons);
                    },
                  ),
                ),
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
