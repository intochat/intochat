import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../core/neuron_method_caller.dart';
import '../nodes/node_context.dart';
import '../theme/ui_tokens.dart' show IcStylePreset;
import 'child_route.dart';

/// Allowed shell-originated `(scope, operation)` requests for `requestApp`.
/// `window` stands for any open window's app (sandbox controls).
const _desktopShellOperations = <(String, String)>{
  ('desktop', 'open'),
  ('desktop', 'close-app'),
  ('desktop', 'widget-open'),
  ('desktop', 'widget-close'),
  ('settings', 'navigate'),
  ('window', 'sandbox-restart'),
  ('window', 'switch-mode'),
};

bool desktopShellOperationAllowed(String scope, String operation) =>
    _desktopShellOperations.contains((scope, operation));

typedef DesktopProjectionLoader = Future<DesktopShellProjection> Function(
  String brainId,
);

typedef ContractReader = Future<Map<String, dynamic>?> Function(
  String contract,
  String key,
  String method,
);

typedef AppRequester = Future<void> Function(
  String app,
  String operation,
  String input,
);

typedef JournalTailWatcher = Stream<List<JournalTailSignal>> Function(
  String brainId,
  int since,
);

/// Unified brain-scoped session controller encapsulating [IntoChatOSUiClient],
/// [NeuronWatchMux], typed [DesktopShellProjection] reads/watches, and
/// deterministic subscription lifecycles for desktop state, user settings,
/// Ino presence, and notifications with full teardown on brain switch.
class BrainSessionController extends ChangeNotifier {
  BrainSessionController({
    required this._brainId,
    this._client,
    DesktopProjectionLoader? loadProjection,
    NeuronLoader? loadNode,
    Stream<void> Function(String kind, String name)? watchNode,
    NeuronSignalWatcher? watchSignal,
    NeuronMethodCaller? callNeuron,
    ContractReader? readContract,
    NeuronAction? onAction,
    ValueChanged<Map<String, dynamic>>? onActivate,
    AppRequester? requestApp,
    JournalTailWatcher? journal,
    bool Function(String app)? knowsApp,
    Iterable<String> installedApps = const [],
    VoiceTranscriber? transcribe,
    VoiceTalker? talk,
    VoiceSpeaker? speak,
    SecretStore? storeSecret,
    ValueChanged<IcStylePreset>? onRemoteStylePreset,
    void Function(Object error)? onDesktopActionError,
    this.reconnectDelay = const Duration(seconds: 2),
    bool desktopEnabled = false,
  }) : _loadProjectionOverride = loadProjection,
       _loadNodeOverride = loadNode,
       _watchNodeOverride = watchNode,
       _watchSignalOverride = watchSignal,
       _callNeuronOverride = callNeuron,
       _readContractOverride = readContract,
       _onActionOverride = onAction,
       _onActivateCallback = onActivate,
       _requestAppOverride = requestApp,
       _journalOverride = journal,
       _knowsAppOverride = knowsApp,
       _installedApps = {for (final a in installedApps) a.toLowerCase()},
       _transcribeOverride = transcribe,
       _talkOverride = talk,
       _speakOverride = speak,
       _storeSecretOverride = storeSecret,
       _onRemoteStylePresetCallback = onRemoteStylePreset,
       _onDesktopActionErrorCallback = onDesktopActionError {
    if (desktopEnabled) {
      enableDesktopSession(true);
    }
  }

  static const _settingsContract = 'IntoChat.OS.Modules.Apps.IUserSettings';
  static const _presenceContract = 'IntoChat.OS.Modules.Apps.IPresence';
  static const _approvalsContract =
      'IntoChat.OS.Modules.Apps.Approvals.IApprovals';
  static const _notificationsContract =
      'IntoChat.OS.Modules.Notifications.INotificationsInbox';
  static const _debuggerContract =
      'IntoChat.OS.Modules.Apps.Debugger.IDebuggerReads';

  final IntoChatOSUiClient? _client;
  final DesktopProjectionLoader? _loadProjectionOverride;
  final NeuronLoader? _loadNodeOverride;
  final Stream<void> Function(String kind, String name)? _watchNodeOverride;
  final NeuronSignalWatcher? _watchSignalOverride;
  final NeuronMethodCaller? _callNeuronOverride;
  final ContractReader? _readContractOverride;
  final NeuronAction? _onActionOverride;
  final ValueChanged<Map<String, dynamic>>? _onActivateCallback;
  final AppRequester? _requestAppOverride;
  final JournalTailWatcher? _journalOverride;
  final bool Function(String app)? _knowsAppOverride;
  final VoiceTranscriber? _transcribeOverride;
  final VoiceTalker? _talkOverride;
  final VoiceSpeaker? _speakOverride;
  final SecretStore? _storeSecretOverride;
  final ValueChanged<IcStylePreset>? _onRemoteStylePresetCallback;
  final void Function(Object error)? _onDesktopActionErrorCallback;

  final Duration reconnectDelay;

  String _brainId;
  int _epoch = 0;
  bool _disposed = false;
  bool _desktopEnabled = false;
  Set<String> _installedApps;

  DesktopShellProjection? _projection;
  Object? _desktopError;
  bool _desktopLoading = false;
  bool _desktopReloadAgain = false;

  bool _smart = false;
  PresenceState? _presenceState;
  String? _syncedStylePreset;
  final StreamController<Presence> _presenceController =
      StreamController<Presence>.broadcast();

  List<OsNotificationItem> _notifications = const [];
  int _unreadNotifications = 0;
  bool _notificationsLoaded = false;
  bool _notificationsLoading = false;

  StreamSubscription<void>? _desktopSub;
  Timer? _desktopReopen;
  StreamSubscription<void>? _settingsSub;
  StreamSubscription<void>? _presenceSub;
  Timer? _presenceReopen;
  StreamSubscription<Map<String, dynamic>>? _notificationsSub;

  String get brainId => _brainId;
  IntoChatOSUiClient? get client => _client;
  DesktopShellProjection? get projection => _projection;
  Object? get desktopError => _desktopError;
  bool get smart => _smart;
  PresenceState? get presenceState => _presenceState;
  Stream<Presence> get presenceStream => _presenceController.stream;
  List<OsNotificationItem> get notifications => _notifications;
  int get unreadNotifications => _unreadNotifications;
  bool get notificationsLoaded => _notificationsLoaded;
  bool get notificationsLoading => _notificationsLoading;
  Set<String> get installedApps => Set.unmodifiable(_installedApps);

  SceneClient? get scenes => _client?.scenes(_brainId);
  VoiceTranscriber? get transcribe =>
      _transcribeOverride ?? _client?.transcribe;
  VoiceTalker? get talk => _talkOverride ?? _client?.talk;
  VoiceSpeaker? get speak => _speakOverride ?? _client?.speak;
  SecretStore? get storeSecret =>
      _storeSecretOverride ?? _client?.storeSecretReference;

  bool get hasNodeWatch => _watchNodeOverride != null || _client != null;

  bool knowsApp(String app) {
    final norm = app.trim().toLowerCase();
    if (norm.isEmpty) return false;
    if (_knowsAppOverride != null &&
        (_knowsAppOverride(app) || _knowsAppOverride(norm))) {
      return true;
    }
    if (_installedApps.contains(norm)) return true;
    final proj = _projection;
    if (proj != null) {
      for (final tile in proj.tiles) {
        if (tile.id.toLowerCase() == norm) return true;
      }
      for (final win in proj.windows) {
        if (win.appId.toLowerCase() == norm) return true;
      }
      for (final pin in proj.pins) {
        if (pin.toLowerCase() == norm) return true;
      }
    }
    return false;
  }

  void updateInstalledApps(Iterable<String> apps) {
    final next = {for (final a in apps) a.toLowerCase()};
    if (next.length == _installedApps.length &&
        next.every(_installedApps.contains)) {
      return;
    }
    _installedApps = next;
    _notify();
  }

  /// Switches the active brain, deterministically tearing down ALL subscriptions
  /// and timers (desktop, settings, presence, notifications) before re-subscribing.
  void switchBrain(String nextBrainId, {bool? desktopEnabled}) {
    if (_disposed) return;
    final nextDesktop = desktopEnabled ?? _desktopEnabled;
    if (nextBrainId == _brainId && nextDesktop == _desktopEnabled) return;
    final brainChanged = nextBrainId != _brainId;
    if (brainChanged) {
      _epoch++;
      _teardownDesktopSubscriptions();
      _resetDesktopState();
      _brainId = nextBrainId;
    }
    _desktopEnabled = nextDesktop;
    if (_desktopEnabled && (brainChanged || _desktopSub == null)) {
      _startDesktopSubscriptions();
    } else if (!_desktopEnabled) {
      _epoch++;
      _teardownDesktopSubscriptions();
      _resetDesktopState();
    }
    _notify();
  }

  void enableDesktopSession(bool enabled, {bool notify = true}) {
    if (_disposed || enabled == _desktopEnabled) return;
    _desktopEnabled = enabled;
    if (enabled) {
      _startDesktopSubscriptions();
    } else {
      _epoch++;
      _teardownDesktopSubscriptions();
      _resetDesktopState();
      if (notify) _notify();
    }
  }

  // ── Unified Transport & Capability Facade ────────────────────────────────

  Future<Map<String, dynamic>> loadNode(String kind, String name) {
    if (_loadNodeOverride != null) return _loadNodeOverride(kind, name);
    final client = _client;
    if (client == null) {
      return Future.error(StateError('No UI client configured for loadNode.'));
    }
    return client.appRequest(_brainId, appNodePath(kind, name));
  }

  Stream<void> watchNode(String kind, String name) {
    if (_watchNodeOverride != null) return _watchNodeOverride(kind, name);
    final client = _client;
    if (client == null) return const Stream.empty();
    return client.watchNode(_brainId, kind, name).map((_) {});
  }

  Stream<Map<String, dynamic>> watchSignal(
    String contract,
    String key,
    String signal,
  ) {
    if (_watchSignalOverride != null) {
      return _watchSignalOverride(contract, key, signal);
    }
    final client = _client;
    if (client == null) return const Stream.empty();
    final scopedKey = key.startsWith('$_brainId/') ? key : '$_brainId/$key';
    return client
        .watches(_brainId)
        .watch(WatchSourceSpec(contract, scopedKey, [signal]))
        .map((s) => s.payload);
  }

  Future<Map<String, dynamic>?> callNeuron(
    String contract,
    String key,
    String method,
    List<Object?> args,
  ) {
    if (_callNeuronOverride != null) {
      return _callNeuronOverride(contract, key, method, args);
    }
    final client = _client;
    if (client == null) return Future.value(null);
    final scopedKey = key.startsWith('$_brainId/') ? key : '$_brainId/$key';
    return client.channel.call(_brainId, contract, scopedKey, method, args);
  }

  Future<Map<String, dynamic>?> readContract(
    String contract,
    String key,
    String method,
  ) {
    if (_readContractOverride != null) {
      return _readContractOverride(contract, key, method);
    }
    final client = _client;
    if (client == null) return Future.value(null);
    return client.channel.call(_brainId, contract, key, method, const []);
  }

  Future<Object?> sendAction(Map<String, Object?> event) {
    if (_onActionOverride != null) return _onActionOverride(event);
    final client = _client;
    if (client == null) return Future.value(null);
    return client.uiEvent(_brainId, event);
  }

  Future<void> requestApp(String app, String operation, String input) async {
    if (_requestAppOverride != null) {
      await _requestAppOverride(app, operation, input);
      return;
    }
    final client = _client;
    if (client == null) return;
    await client.requestApp(_brainId, app, operation, input);
  }

  void activate(Map<String, dynamic> action) =>
      _onActivateCallback?.call(action);

  /// Only the Creator's view gets the journal tail for live brain graph pulses.
  Stream<List<JournalTailSignal>> Function()? journalFor(String view) {
    final watchJournal =
        _journalOverride ??
        (_client == null
            ? null
            : (String b, int since) => _client.watchJournal(b, since: since));
    if (watchJournal == null ||
        (_readContractOverride == null && _client == null) ||
        !view.startsWith('$_brainId/apps/creator/')) {
      return null;
    }
    final brain = _brainId;
    return () async* {
      Object? head;
      try {
        head = (await readContract(
          _debuggerContract,
          'creator/reads',
          'Head',
        ))?['value'];
      } catch (_) {
        return;
      }
      if (head is! num) return;
      yield* watchJournal(brain, head.toInt());
    };
  }

  // ── Typed Desktop Commands ───────────────────────────────────────────────

  Future<void> dispatchDesktopSignal(
    String signal, [
    Map<String, Object?>? args,
  ]) async {
    final epoch = _epoch;
    try {
      await sendAction({
        'kind': 'act',
        'neuron': '$_brainId/apps/desktop',
        'target': '.',
        'signal': signal,
        'args': ?args,
      });
      if (!_disposed && epoch == _epoch) {
        await reloadDesktop();
      }
    } catch (error) {
      if (!_disposed && epoch == _epoch) {
        unawaited(reloadDesktop());
        _onDesktopActionErrorCallback?.call(error);
      }
    }
  }

  Future<void> openApp(String appId) => appId == 'settings'
      ? dispatchDesktopSignal('desktop.settings')
      : dispatchDesktopSignal('desktop.open', {'app': appId});

  Future<void> focusWindow(String windowId) =>
      dispatchDesktopSignal('desktop.focus', {'windowId': windowId});

  Future<void> closeWindow(String windowId, {int? tabIndex}) =>
      dispatchDesktopSignal('desktop.close', {
        'target': tabIndex != null ? '$windowId/$tabIndex' : windowId,
      });

  Future<void> closeApp(String appId) =>
      dispatchDesktopSignal('desktop.close-app', {'app': appId});

  Future<void> selectTab(String windowId, int tabIndex) =>
      dispatchDesktopSignal('desktop.tab', {'target': '$windowId/$tabIndex'});

  Future<void> newTab([String? windowId]) => dispatchDesktopSignal(
    'desktop.newtab',
    windowId == null ? null : {'windowId': windowId},
  );

  Future<void> toggleFavourite(String windowId) =>
      dispatchDesktopSignal('desktop.favourite', {'windowId': windowId});

  Future<void> pinApp(String appId) =>
      dispatchDesktopSignal('desktop.pin', {'appId': appId, 'pinned': true});

  Future<void> unpinApp(String appId) =>
      dispatchDesktopSignal('desktop.unpin', {'appId': appId});

  Future<void> openWidget(String widgetId) =>
      dispatchDesktopSignal('desktop.widget-open', {'widget': widgetId});

  Future<void> closeWidget(String widgetId) =>
      dispatchDesktopSignal('desktop.widget-close', {'widget': widgetId});

  Future<void> autoArrangeDesktop() =>
      dispatchDesktopSignal('desktop.auto-arrange');

  Future<void> requestShellOperation(
    String app,
    String operation,
    String input,
  ) async {
    final scope = app == 'desktop' || app == 'settings' ? app : 'window';
    if (!desktopShellOperationAllowed(scope, operation)) return;
    if (scope == 'window' && !knowsApp(app)) return;
    final epoch = _epoch;
    try {
      await requestApp(app, operation, input);
      if (!_disposed && epoch == _epoch) {
        await reloadDesktop();
      }
    } catch (error) {
      if (!_disposed && epoch == _epoch) {
        unawaited(reloadDesktop());
        _onDesktopActionErrorCallback?.call(error);
      }
    }
  }

  // ── Desktop Projection, Settings, Presence & Notifications Lifecycles ────

  void _startDesktopSubscriptions() {
    final epoch = _epoch;
    unawaited(reloadDesktop());
    _subscribeDesktop(epoch);
    _subscribeSettings(epoch);
    _subscribeNotifications(epoch);
    unawaited(loadNotifications(notifyStart: false));
  }

  void _teardownDesktopSubscriptions() {
    _desktopSub?.cancel();
    _desktopSub = null;
    _desktopReopen?.cancel();
    _desktopReopen = null;
    _settingsSub?.cancel();
    _settingsSub = null;
    _presenceSub?.cancel();
    _presenceSub = null;
    _presenceReopen?.cancel();
    _presenceReopen = null;
    _notificationsSub?.cancel();
    _notificationsSub = null;
  }

  void _resetDesktopState() {
    _projection = null;
    _desktopError = null;
    _desktopLoading = false;
    _desktopReloadAgain = false;
    _smart = false;
    _presenceState = null;
    _syncedStylePreset = null;
    _notifications = const [];
    _unreadNotifications = 0;
    _notificationsLoaded = false;
    _notificationsLoading = false;
  }

  void _subscribeDesktop(int epoch) {
    _desktopSub?.cancel();
    _desktopSub = null;
    if (!hasNodeWatch) return;
    _desktopSub = watchNode('screen', '$_brainId/app.desktop/default').listen(
      (_) {
        if (!_disposed && epoch == _epoch) unawaited(reloadDesktop());
      },
      onError: (Object _) => _scheduleDesktopReopen(epoch),
      onDone: () => _scheduleDesktopReopen(epoch),
    );
  }

  void _scheduleDesktopReopen(int epoch) {
    if (_disposed || epoch != _epoch || _desktopReopen != null) return;
    _desktopReopen = Timer(reconnectDelay, () {
      _desktopReopen = null;
      if (_disposed || epoch != _epoch) return;
      unawaited(reloadDesktop());
      _subscribeDesktop(epoch);
    });
  }

  Future<void> reloadDesktop() async {
    if (_disposed) return;
    if (_desktopLoading) {
      _desktopReloadAgain = true;
      return;
    }
    _desktopLoading = true;
    try {
      do {
        _desktopReloadAgain = false;
        final epoch = _epoch;
        final brain = _brainId;
        try {
          final projection = await _fetchProjection(brain);
          if (!_disposed && epoch == _epoch) {
            _projection = projection;
            _desktopError = null;
            _notify();
          }
        } catch (error) {
          if (!_disposed && epoch == _epoch && _projection == null) {
            _desktopError = error;
            _notify();
          }
        }
      } while (!_disposed && _desktopReloadAgain);
    } finally {
      _desktopLoading = false;
    }
  }

  Future<DesktopShellProjection> _fetchProjection(String brain) {
    if (_loadProjectionOverride != null) {
      return _loadProjectionOverride(brain);
    }
    final client = _client;
    if (client == null) {
      return Future.value(const DesktopShellProjection());
    }
    return client.readDesktopProjection(brain);
  }

  void _subscribeSettings(int epoch) {
    unawaited(_readSettings(epoch));
    if (!hasNodeWatch) return;
    _settingsSub?.cancel();
    _settingsSub = watchNode('os.settings', 'settings').listen((_) {
      if (!_disposed && epoch == _epoch) unawaited(_readSettings(epoch));
    }, onError: (Object _) {});
  }

  static IcStylePreset? parseStylePreset(String? raw) =>
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

  Future<void> _readSettings(int epoch) async {
    if (_readContractOverride == null && _client == null) return;
    var on = false;
    String? remotePreset;
    try {
      final settings = await readContract(
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
    if (_disposed || epoch != _epoch) return;
    if (remotePreset != null && remotePreset != _syncedStylePreset) {
      _syncedStylePreset = remotePreset;
      final parsed = parseStylePreset(remotePreset);
      if (parsed != null) {
        _onRemoteStylePresetCallback?.call(parsed);
      }
    }
    if (on == _smart) return;
    _smart = on;
    if (!on) {
      _presenceState = null;
      if (_client == null) _unreadNotifications = 0;
      _presenceSub?.cancel();
      _presenceSub = null;
      _presenceReopen?.cancel();
      _presenceReopen = null;
      _notify();
      return;
    }
    _notify();
    _subscribePresence(epoch);
    unawaited(_readPresence(epoch));
  }

  void _subscribePresence(int epoch) {
    _presenceSub?.cancel();
    _presenceSub = null;
    if (!hasNodeWatch) return;
    _presenceSub = watchNode('ino.presence', 'ino/presence').listen(
      (_) {
        if (!_disposed && epoch == _epoch) unawaited(_readPresence(epoch));
      },
      onError: (Object _) => _presenceDropped(epoch),
      onDone: () => _presenceDropped(epoch),
    );
  }

  void _presenceDropped(int epoch) {
    if (_disposed || epoch != _epoch || !_smart) return;
    _presenceController.add(
      const Presence(state: PresenceState.disconnected, revision: -1),
    );
    _presenceState = PresenceState.disconnected;
    _notify();
    _presenceReopen ??= Timer(reconnectDelay, () {
      _presenceReopen = null;
      if (_disposed || epoch != _epoch || !_smart) return;
      _subscribePresence(epoch);
      unawaited(_readPresence(epoch));
    });
  }

  Future<void> _readPresence(int epoch) async {
    try {
      final json = await readContract(
        _presenceContract,
        'ino/presence',
        'Read',
      );
      if (json == null || _disposed || epoch != _epoch || !_smart) return;
      final presence = Presence.fromJson(json);
      _presenceController.add(presence);
      _presenceState = presence.state;
      _notify();
    } catch (_) {
      // Next change signal reads again.
    }
    await _readApprovals(epoch);
  }

  Future<void> _readApprovals(int epoch) async {
    var count = 0;
    try {
      final reply = await readContract(
        _approvalsContract,
        'approvals/index',
        'Pending',
      );
      final pending = reply?['value'];
      count = pending is List ? pending.length : 0;
    } catch (_) {
      count = 0;
    }
    if (!_disposed && epoch == _epoch && _smart && _client == null) {
      _unreadNotifications = count;
      _notify();
    }
  }

  void _subscribeNotifications(int epoch) {
    _notificationsSub?.cancel();
    _notificationsSub = null;
    if (_watchSignalOverride == null &&
        (_client == null || !_client.grpcLive)) {
      return;
    }
    try {
      _notificationsSub =
          watchSignal(
            _notificationsContract,
            'inbox',
            'NotificationSent',
          ).listen((_) {
            if (!_disposed && epoch == _epoch) {
              unawaited(loadNotifications());
            }
          }, onError: (Object _) {});
    } catch (_) {
      // Live watch is optional; opening the popover refreshes over HTTP.
    }
  }

  Future<void> loadNotifications({bool notifyStart = true}) async {
    final client = _client;
    if (client == null || _disposed) return;
    final epoch = _epoch;
    final brain = _brainId;
    if (!_notificationsLoaded) {
      _notificationsLoading = true;
      if (notifyStart) _notify();
    }
    try {
      final result = await client.listNotifications(brain);
      if (_disposed || epoch != _epoch) return;
      _notifications = result.items;
      _unreadNotifications = result.unreadCount;
      _notificationsLoaded = true;
      _notificationsLoading = false;
      _notify();
    } catch (_) {
      if (_disposed || epoch != _epoch) return;
      _notificationsLoading = false;
      _notify();
    }
  }

  void markNotificationRead(String id) {
    var decremented = false;
    _notifications = [
      for (final item in _notifications)
        if (item.id == id && !item.read)
          (() {
            decremented = true;
            return item.copyWith(read: true);
          })()
        else
          item,
    ];
    if (decremented && _unreadNotifications > 0) {
      _unreadNotifications--;
    }
    _notify();
    final client = _client;
    if (client == null) return;
    final epoch = _epoch;
    unawaited(
      client
          .markNotificationRead(_brainId, id)
          .then((_) {
            if (!_disposed && epoch == _epoch) return loadNotifications();
          })
          .catchError((Object _) {}),
    );
  }

  void markAllNotificationsRead() {
    _notifications = [
      for (final item in _notifications)
        item.read ? item : item.copyWith(read: true),
    ];
    _unreadNotifications = 0;
    _notify();
    final client = _client;
    if (client == null) return;
    final epoch = _epoch;
    unawaited(
      client
          .markAllNotificationsRead(_brainId)
          .then((_) {
            if (!_disposed && epoch == _epoch) return loadNotifications();
          })
          .catchError((Object _) {}),
    );
  }

  void clearNotifications() {
    _notifications = const [];
    _unreadNotifications = 0;
    _notify();
    final client = _client;
    if (client == null) return;
    final epoch = _epoch;
    unawaited(
      client
          .clearNotifications(_brainId)
          .then((_) {
            if (!_disposed && epoch == _epoch) return loadNotifications();
          })
          .catchError((Object _) {}),
    );
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    if (_disposed) return;
    _disposed = true;
    _epoch++;
    _teardownDesktopSubscriptions();
    unawaited(_presenceController.close());
    super.dispose();
  }
}

/// Provides the active [BrainSessionController] down the shell host tree
/// (`WorkspaceApp` -> `LiveDesktopShell` -> `ViewHost` -> `ApplicationSurface`)
/// without prop-drilling transport or capability closures.
class BrainSessionScope extends InheritedNotifier<BrainSessionController> {
  const BrainSessionScope({
    super.key,
    required BrainSessionController controller,
    required super.child,
  }) : super(notifier: controller);

  static BrainSessionController of(BuildContext context) {
    final scope = context
        .dependOnInheritedWidgetOfExactType<BrainSessionScope>();
    assert(scope != null, 'No BrainSessionScope found in widget tree.');
    return scope!.notifier!;
  }

  static BrainSessionController? maybeOf(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<BrainSessionScope>()?.notifier;

  /// Non-subscribing O(1) lookup for widgets that only invoke controller
  /// transport/capability methods (e.g. [ViewHost], [ApplicationSurface])
  /// without rebuilding on desktop projection or notification updates.
  static BrainSessionController? readOf(BuildContext context) =>
      context.getInheritedWidgetOfExactType<BrainSessionScope>()?.notifier;
}
