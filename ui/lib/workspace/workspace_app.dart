import 'dart:async';

import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:uuid/uuid.dart';

import '../connections/connect_window.dart';
import '../desktop/command_palette.dart';
import '../desktop/desktop_overlays.dart';
import '../desktop/live_desktop_shell.dart';
import '../desktop/smart_desktop.dart';
import 'apps/app_activations.dart';
import 'shell_activation_router.dart';
import 'width_hint.dart';
import 'known_apps.dart';
import 'workspace_store.dart';
import 'workspace_link.dart';
import 'compute_usage_panel.dart';
import 'workspace_preferences_io.dart'
    if (dart.library.js_interop) 'workspace_preferences_web.dart'
    as preferences;

export 'shell_activation_router.dart' show settingsActivation;

class WorkspaceApp extends StatefulWidget {
  const WorkspaceApp({
    super.key,
    this.store,
    this.onSwitchAccount,
    this.initialLocation,
    this.persistenceKey = 'intochatos.preferences.v1',
    this.onOpenUrl,
    this.kernelBaseUri,
    this.statusMessage,
    this.programmingClient,
    this.isGuest,
    this.isEphemeral,
    this.guestRemaining,
    this.onSaveGuestBrain,
  });
  final WorkspaceStore? store;
  final Future<void> Function()? onSwitchAccount;
  final Uri? initialLocation, kernelBaseUri;
  final String persistenceKey;
  final OpenUrl? onOpenUrl;
  final String? statusMessage;
  final IntoChatOSUiClient? programmingClient;
  final bool? isGuest;
  final bool? isEphemeral;
  final Duration? guestRemaining;
  final VoidCallback? onSaveGuestBrain;
  @override
  State<WorkspaceApp> createState() => WorkspaceAppState();
}

class WorkspaceAppState extends State<WorkspaceApp>
    with WidgetsBindingObserver {
  // A connector may mount any app-authored root. Product composition stays in that app.
  static const _root = String.fromEnvironment('INTOCHATOS_ROOT');
  static String get _effectiveRoot {
    final q = Uri.base.queryParameters['root']?.trim() ?? '';
    return q.isNotEmpty ? q : _root;
  }

  static const _rootKind = String.fromEnvironment(
    'INTOCHATOS_ROOT_KIND',
    defaultValue: 'layout',
  );
  late final WorkspaceStore store =
      widget.store ??
      WorkspaceStore(
        readPreferences: () =>
            preferences.readPreferences(widget.persistenceKey),
        writePreferences: (value) =>
            preferences.writePreferences(widget.persistenceKey, value),
      );
  late final _localPreferences = WorkspaceLocalPreferences(store);

  bool get _isGuestSession => resolveIsGuestSession(
    isGuest: widget.isGuest,
    isEphemeral: widget.isEphemeral,
    clientIsGuest: widget.programmingClient?.isGuestSession ?? false,
    brainId: _brain,
  );

  Duration? get _guestRemaining =>
      widget.guestRemaining ?? widget.programmingClient?.guestRemaining;

  Uri? get _downloadHubUri =>
      (widget.kernelBaseUri ?? widget.programmingClient?.baseUri)?.resolve(
        '/download',
      );

  void _triggerSaveGuestBrain() {
    if (widget.onSaveGuestBrain != null) {
      widget.onSaveGuestBrain!();
      return;
    }
    if (widget.onSwitchAccount != null) {
      unawaited(widget.onSwitchAccount!());
    }
  }

  // The apps an OpenWindow intent may open: the launcher's and the brain's installed apps.
  Set<String> _knownApps = knownAppsFor(const []);
  BrainSessionController? _session;

  late final ShellActivationRouter _activationRouter = ShellActivationRouter(
    readState: () => ShellRoutingState(
      currentBrain: _brain,
      isDesktopRoot: _isDesktopRoot,
      hasExplicitRoot: _hasExplicitRoot,
      activationApp: _activationApp,
      rootName: _rootName,
      focusedNeuron: _focusedNeuron,
    ),
    sendAct: (brain, event) {
      final client = widget.programmingClient;
      if (client == null) return Future<Object?>.value(null);
      return client.uiEvent(brain, event);
    },
    requestApp: (brain, app, operation, input) async {
      final client = widget.programmingClient;
      if (client == null) return;
      await client.requestApp(brain, app, operation, input);
    },
    listBrainIds: () async {
      final client = widget.programmingClient;
      if (client == null) return const [];
      return [for (final entry in await client.listBrains()) entry.id];
    },
    selectBrain: (brain, {show}) => _selectBrain(brain, show: show),
    onSwitchAccount: () async => await widget.onSwitchAccount?.call(),
    onEndLobby: () async => await widget.programmingClient?.endLobby(),
    onReconnect: _reconnect,
    onAppLifecycle: (brain, activation) async {
      final client = widget.programmingClient;
      if (client == null) return;
      final changingBrain = brain != _brain;
      final result = await runAppActivation(
        activation.toWireMap(brainId: brain),
        client.jsonRequest,
      );
      if (!mounted) return;
      if (!changingBrain) unawaited(_readKnownApps(brain));
      if (result.toast.isNotEmpty) _toast(result.toast, tone: result.tone);
      if (result.reopen case final String reopen) {
        await _activate(
          WorkspaceAppActivation(
            brainId: brain,
            application: 'apps',
            operation: 'open',
            input: reopen,
            hasNavigationPayload: true,
          ),
        );
      }
      if (result.open case final String open) {
        await _activate(
          WorkspaceAppActivation(
            brainId: brain,
            application: open,
            operation: 'open',
          ),
        );
      }
    },
    onOpenConnections: (brain, activation) {
      final client = widget.programmingClient;
      if (client == null || !mounted) return;
      final wireAction = activation.toWireMap(brainId: brain);
      showDialog<void>(
        context: _navigator.currentContext!,
        builder: (_) => Dialog(
          child: SizedBox(
            width: 560,
            height: 520,
            child: ConnectWindow(
              // Connect preselects its connector; the connection is the one the activation names, else the connector.
              initialConnectorId: activation.connectorId,
              initialConnectionId: connectWindowConnectionId(wireAction),
              request: (path, {body}) =>
                  client.workspaceConnectionsRequest(brain, path, body: body),
              connectorRequest: (path, {body}) =>
                  client.connectorRequest(brain, path, body: body),
              windowsRequest: (path, {body}) =>
                  client.workspaceWindowsRequest(brain, path, body: body),
              onOpen: widget.onOpenUrl,
            ),
          ),
        ),
      );
    },
    onShowNeuron: (brain, neuron) async {
      final client = widget.programmingClient;
      if (client == null) return;
      final snap = await client.showNeuron(
        brain,
        neuron,
        const Uuid().v4(),
        _workspaceSnapshot?.revision ?? 0,
      );
      if (mounted) {
        setState(() {
          _departureRefusal = null;
          _workspaceSnapshot = snap;
          _focusedNeuron = snap.path.isNotEmpty ? snap.path.last : neuron;
        });
      }
    },
    onSwitchExplicitSurface: (brain, app, neuron) {
      if (!mounted) return;
      setState(() {
        _departureRefusal = null;
        _rootName = '$brain/apps/$app/view';
        _renderKind = 'view';
        _focusedNeuron = neuron;
        _surfaceReloadEpoch++;
      });
    },
    onDepartureRefused: (refusal) {
      if (!mounted) return;
      setState(() {
        _departureRefusal = refusal;
        _surfaceReloadEpoch++;
      });
    },
    onRegisterKnownApp: (normApp) {
      if (!_knownApps.contains(normApp)) {
        final next = {..._knownApps, normApp};
        _session?.updateInstalledApps(next);
        if (mounted) setState(() => _knownApps = next);
      }
    },
    onToast: _toast,
    isMounted: () => mounted,
  );

  void _syncSession(String brain, {required bool isDesktopRoot}) {
    final client = widget.programmingClient;
    if (client == null) return;
    final existing = _session;
    if (existing == null) {
      _session = BrainSessionController(
        brainId: brain,
        client: client,
        installedApps: _knownApps,
        onActivate: (action) => unawaited(_activate(action)),
        onRemoteStylePreset: (selected) =>
            unawaited(_tool('theme:${selected.id}')),
        onDesktopActionError: (_) {
          if (mounted) {
            _toast('The desktop changed. Try again.', tone: UiTone.error);
          }
        },
        desktopEnabled: isDesktopRoot,
      );
    } else {
      existing.switchBrain(brain, desktopEnabled: isDesktopRoot);
      existing.updateInstalledApps(_knownApps);
    }
  }

  Future<void> _readKnownApps(String brain) async {
    try {
      final installed = await widget.programmingClient!.listInstalledApps(
        brain,
      );
      if (mounted && (_brain == null || _brain == brain)) {
        final next = knownAppsFor([
          for (final app in installed) app.id,
          for (final tile
              in _session?.projection?.tiles ?? const <DesktopAppTile>[])
            tile.id,
        ]);
        _session?.updateInstalledApps(next);
        setState(() => _knownApps = next);
      }
    } catch (_) {
      // An installed app opens again after the next read.
    }
  }

  final _navigator = GlobalKey<NavigatorState>();
  String? _rootName;
  String? _activationApp;
  String _renderKind = 'layout';
  bool _hasExplicitRoot = false;
  String? _brain;
  String? _focusedNeuron;
  WorkspaceSnapshot? _workspaceSnapshot;
  Object? _startupError;
  bool _ready = false;
  bool _paletteOpen = false;
  BarLayout _barLayout = BarLayout.floatingGlass;
  Uri? _pendingLocation;
  String? _departureRefusal;
  int _surfaceReloadEpoch = 0;
  UiThemeSeeds _themeSeeds = UiThemeSeeds.warmPaper;
  bool _hasCustomThemeSeeds = false;

  UiThemeSeeds get themeSeeds => _themeSeeds;

  void setThemeSeeds(UiThemeSeeds seeds) {
    if (!mounted) return;
    setState(() {
      _themeSeeds = seeds;
      _hasCustomThemeSeeds = true;
    });
  }

  void resetThemeSeeds() {
    if (!mounted) return;
    setState(() {
      _hasCustomThemeSeeds = false;
    });
  }

  String? get currentBrain => _brain;
  WorkspaceSnapshot? get workspaceSnapshot => _workspaceSnapshot;
  String get focusedNeuron =>
      _focusedNeuron ??
      (_workspaceSnapshot != null && _workspaceSnapshot!.path.isNotEmpty
          ? _workspaceSnapshot!.path.last
          : '${_brain ?? ''}/apps/ino');
  Future<void> activateShell(ShellActivation activation) =>
      _activate(activation);
  Future<void> activateAction(Map<String, dynamic> action) =>
      _activateWire(action);
  Future<void> show(String neuron) async {
    final client = widget.programmingClient;
    final brain = _brain;
    if (client == null || brain == null) return;
    final snap = await client.showNeuron(
      brain,
      neuron,
      const Uuid().v4(),
      _workspaceSnapshot?.revision ?? 0,
    );
    if (mounted) {
      setState(() {
        _workspaceSnapshot = snap;
        _focusedNeuron = snap.path.isNotEmpty ? snap.path.last : neuron;
      });
    }
  }

  Future<void> home() async {
    final client = widget.programmingClient;
    final brain = _brain;
    if (client == null || brain == null) return;
    final snap = await client.homeNeuron(
      brain,
      const Uuid().v4(),
      _workspaceSnapshot?.revision ?? 0,
    );
    if (mounted) {
      setState(() {
        _workspaceSnapshot = snap;
        _focusedNeuron = snap.path.isNotEmpty ? snap.path.last : brain;
      });
    }
  }

  @override
  void initState() {
    super.initState();
    _pendingLocation = widget.initialLocation;
    WidgetsBinding.instance.addObserver(this);
    store.addListener(_changed);
    unawaited(_start());
  }

  @override
  void didChangePlatformBrightness() {
    if (mounted && !_hasCustomThemeSeeds) setState(() {});
  }

  @override
  Future<bool> didPushRouteInformation(
    RouteInformation routeInformation,
  ) async {
    final activation = workspaceLinkActivation(routeInformation.uri);
    if (activation == null) return false;
    if (!_ready) {
      _pendingLocation = routeInformation.uri;
    } else {
      await _openLink(routeInformation.uri, activation);
    }
    return true;
  }

  Future<void> _openLink(Uri location, ShellActivation activation) async {
    await _activate(activation);
    if (!mounted) return;
    final route = location.fragment.startsWith('/')
        ? Uri.parse(location.fragment)
        : location;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) {
        unawaited(
          SystemNavigator.routeInformationUpdated(uri: route, replace: true),
        );
      }
    });
    WidgetsBinding.instance.scheduleFrame();
  }

  void _changed() {
    if (mounted) setState(() {});
  }

  Future<void> _start() async {
    await store.load();
    final client = widget.programmingClient;
    if (client == null || !mounted) return;
    try {
      final brain = client.defaultBrainId;
      if (brain == null) throw StateError('No brain selected.');
      await _selectBrain(brain);
      if (!mounted) return;
      _ready = true;
      final location = _pendingLocation;
      _pendingLocation = null;
      if (workspaceLinkActivation(location) case final activation?) {
        await _openLink(location!, activation);
      }
      if (!_hasExplicitRoot &&
          widget.initialLocation?.path.startsWith('/settings') == true) {
        unawaited(_activate(WorkspaceAppActivation.settings()));
      }
    } catch (error) {
      if (mounted) setState(() => _startupError = error);
    }
  }

  Future<void> _selectBrain(
    String brain, {
    String? show,
    bool reconnect = false,
  }) async {
    if (_brain == brain && !reconnect) return;
    unawaited(_readKnownApps(brain));
    final root = _effectiveRoot;
    if (root.isNotEmpty) {
      final rootName = '$brain/$root';
      final isDesktop =
          _rootKind == 'view' && rootName == '$brain/apps/desktop/view';
      _syncSession(brain, isDesktopRoot: isDesktop);
      setState(() {
        _brain = brain;
        _rootName = rootName;
        _renderKind = _rootKind;
        _hasExplicitRoot = true;
      });
      return;
    }
    final client = widget.programmingClient!;
    final connection = await client.appRequest(brain, 'connect', body: {});
    if (connection['root'] case final Map root) {
      final name = root['name'] as String;
      final kind = root['kind'] as String;
      if (!mounted) return;
      final isDesktop = kind == 'view' && name == '$brain/apps/desktop/view';
      _syncSession(brain, isDesktopRoot: isDesktop);
      setState(() {
        _brain = brain;
        _rootName = name;
        _activationApp = connection['activationApp'] as String?;
        _renderKind = kind;
        _hasExplicitRoot = true;
      });
      return;
    }
    var snap = await client.readWorkspace(brain);
    if (show != null && (snap.path.isEmpty || snap.path.last != show)) {
      snap = await client.showNeuron(
        brain,
        show,
        const Uuid().v4(),
        snap.revision,
      );
    }
    if (!mounted) return;
    _syncSession(brain, isDesktopRoot: true);
    setState(() {
      _brain = brain;
      _rootName = '$brain/apps/desktop/view';
      _activationApp = connection['activationApp'] as String?;
      _renderKind = 'view';
      _hasExplicitRoot = false;
      _workspaceSnapshot = snap;
      _focusedNeuron = snap.path.isNotEmpty
          ? snap.path.last
          : (show ?? '$brain/apps/ino');
    });
  }

  // The desktop root lays out one window on a narrow screen: tell it which side of 900 px the window is.
  Widget _widthHinted(Widget root) {
    final brain = _brain!;
    if (!_hasExplicitRoot ||
        _renderKind != 'view' ||
        _rootName != '$brain/apps/desktop/view') {
      return root;
    }
    return WidthHint(
      key: ValueKey('width:$_rootName'),
      onNarrow: (narrow) {
        final session = _session;
        if (session == null) return;
        unawaited(
          session.dispatchDesktopSignal('desktop.narrow', {
            'narrow': '$narrow',
          }),
        );
      },
      child: root,
    );
  }

  // The request is handled asynchronously, so connect again (a few times, briefly) until the root changes.
  Future<void> _reconnect(ReconnectActivation activation) async {
    final brain = _brain!;
    final app = activation.application;
    final operation = activation.operation;
    if (app != null && operation != null) {
      await widget.programmingClient!.requestApp(
        brain,
        app,
        operation,
        activation.input ?? '',
      );
    }
    // A fixed root (INTOCHATOS_ROOT or ?root=...) never changes on connect: say so at once.
    if (_effectiveRoot.isEmpty) {
      final before = _rootName;
      for (var attempt = 0; attempt < 10 && mounted; attempt++) {
        await _selectBrain(brain, reconnect: true);
        if (_rootName != before || operation == null) return;
        await Future<void>.delayed(const Duration(milliseconds: 300));
      }
    }
    if (!mounted) return;
    _toast('Layout will change on next start');
  }

  void _toast(String message, {UiTone tone = UiTone.neutral}) {
    final overlay = _navigator.currentState?.overlay?.context;
    if (overlay != null) showKitToast(overlay, message, tone: tone);
  }

  Future<void> _activate(ShellActivation activation) async {
    if (widget.programmingClient == null) return;
    await _activationRouter.activate(activation);
  }

  Future<void> _activateWire(Map<String, dynamic> action) async {
    if (widget.programmingClient == null) return;
    await _activationRouter.activateWire(action);
  }

  Future<void> _tool(String name) async {
    final client = widget.programmingClient!;
    final navigator = _navigator.currentState!;
    try {
      switch (name) {
        case 'preferences':
          await _activate(WorkspaceAppActivation.settings());
        case 'connections':
          await _activate(const ConnectionsActivation());
        case 'compute':
          navigator.push(
            MaterialPageRoute<void>(
              builder: (_) => Scaffold(
                body: ComputeUsagePanel(
                  client: client,
                  workspaceId: _brain!,
                  workspaceName: _brain!,
                  onClose: () => navigator.pop(),
                ),
              ),
            ),
          );
        case 'new':
          final brain = await client.createWorkspace();
          if (mounted) await _selectBrain(brain);
        case 'gallery':
          await _activate(
            const WorkspaceAppActivation(
              application: 'gallery',
              operation: 'open',
            ),
          );
        case 'palette':
          setState(() => _paletteOpen = !_paletteOpen);
        case 'smart-desktop':
          navigator.push(
            MaterialPageRoute<void>(
              builder: (_) => Scaffold(
                appBar: AppBar(title: const Text('Smart Desktop · 3D Brain')),
                body: SmartDesktop(
                  stylePreset: _currentStylePreset(),
                  barLayout: _barLayout,
                  onLaunchApp: (appId) {
                    navigator.pop();
                    unawaited(
                      _activate(
                        WorkspaceAppActivation(
                          application: appId,
                          operation: 'open',
                        ),
                      ),
                    );
                  },
                  onOpenPalette: () {
                    navigator.pop();
                    setState(() => _paletteOpen = true);
                  },
                  onSelectPreset: (preset) =>
                      unawaited(_tool('theme:${preset.id}')),
                  onCycleBarLayout: () {
                    setState(() {
                      _barLayout =
                          BarLayout.values[(_barLayout.index + 1) %
                              BarLayout.values.length];
                    });
                  },
                ),
              ),
            ),
          );
        case final String preset when preset.startsWith('theme:'):
          await _localPreferences.write(
            PreferenceStore.localTheme,
            preset.substring('theme:'.length),
          );
          if (mounted && _hasCustomThemeSeeds) {
            setState(() => _hasCustomThemeSeeds = false);
          }
        case 'switch':
          await widget.onSwitchAccount?.call();
      }
    } catch (_) {
      if (mounted) {
        _toast('Could not open that tool. Try again.', tone: UiTone.error);
      }
    }
  }

  IcStylePreset _currentStylePreset() => _hasCustomThemeSeeds
      ? _themeSeeds.preset
      : IcStylePreset.fromId(store.settings.theme);

  UiThemeSeeds _resolveActiveSeeds(BuildContext context) {
    final effectiveTheme = _effectiveTheme;
    final themeMode = UiTheme.modeFor(effectiveTheme);
    final platformBrightness =
        MediaQuery.maybePlatformBrightnessOf(context) ??
        WidgetsBinding.instance.platformDispatcher.platformBrightness;
    final effectiveBrightness = switch (themeMode) {
      ThemeMode.dark => Brightness.dark,
      ThemeMode.light => Brightness.light,
      ThemeMode.system => platformBrightness,
    };
    final baseSeeds = _hasCustomThemeSeeds
        ? _themeSeeds
        : UiThemeSeeds.fromStoredTheme(
            effectiveTheme,
            brightness: effectiveBrightness,
          );
    return store.settings.reducedMotion
        ? baseSeeds.copyWith(motion: UiMotionMode.reduced)
        : baseSeeds;
  }

  bool get _isDesktopRoot =>
      _brain != null &&
      _renderKind == 'view' &&
      _rootName == '$_brain/apps/desktop/view';

  String get _effectiveTheme => store.settings.theme;

  Widget _menu() => PopupMenuButton<String>(
    tooltip: 'Profile',
    icon: const Icon(Icons.person_outline),
    onSelected: (value) => unawaited(_tool(value)),
    itemBuilder: (_) => [
      const PopupMenuItem(
        value: 'preferences',
        child: Text('Workspace configuration'),
      ),
      const PopupMenuItem(value: 'connections', child: Text('Connections')),
      const PopupMenuItem(value: 'compute', child: Text('Compute')),
      const PopupMenuItem(value: 'gallery', child: Text('UI Gallery')),
      const PopupMenuItem(
        value: 'smart-desktop',
        child: Text('Smart Desktop (3D)'),
      ),
      const PopupMenuItem(
        value: 'palette',
        child: Text('Command Palette (⌘K)'),
      ),
      const PopupMenuItem(value: 'new', child: Text('New workspace')),
      if (widget.onSwitchAccount != null)
        const PopupMenuItem(value: 'switch', child: Text('Switch account')),
    ],
  );
  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _session?.dispose();
    store.removeListener(_changed);
    if (widget.store == null) store.dispose();
    super.dispose();
  }

  UiThemeSeeds? _cachedSeeds;
  ThemeData? _cachedThemeData;

  ThemeData _themeDataFor(UiThemeSeeds seeds) {
    if (_cachedSeeds == seeds && _cachedThemeData != null) {
      return _cachedThemeData!;
    }
    _cachedSeeds = seeds;
    return _cachedThemeData = seeds.toThemeData();
  }

  @override
  Widget build(BuildContext context) {
    final activeSeeds = _resolveActiveSeeds(context);
    final preset = activeSeeds.preset;
    final themeData = _themeDataFor(activeSeeds);
    return MaterialApp(
      navigatorKey: _navigator,
      debugShowCheckedModeBanner: false,
      theme: themeData,
      darkTheme: themeData,
      themeMode: activeSeeds.brightness == Brightness.dark
          ? ThemeMode.dark
          : ThemeMode.light,
      builder: (context, child) {
        return UiThemeSeedsController(
          seeds: activeSeeds,
          setSeeds: setThemeSeeds,
          resetSeeds: resetThemeSeeds,
          child: UiSeedThemeScope(
            seeds: activeSeeds,
            wrapTheme: false,
            child: MediaQuery(
              data: MediaQuery.of(context).copyWith(
                disableAnimations:
                    store.settings.reducedMotion ||
                    MediaQuery.disableAnimationsOf(context),
              ),
              child: child!,
            ),
          ),
        );
      },
      home: CallbackShortcuts(
        bindings: <ShortcutActivator, VoidCallback>{
          const SingleActivator(LogicalKeyboardKey.keyK, control: true): () =>
              setState(() => _paletteOpen = !_paletteOpen),
          const SingleActivator(LogicalKeyboardKey.keyK, meta: true): () =>
              setState(() => _paletteOpen = !_paletteOpen),
        },
        child: Scaffold(
          body: Stack(
            fit: StackFit.expand,
            children: [
              _rootName != null && _brain != null && _session != null
                  ? BrainSessionScope(
                      controller: _session!,
                      child: SafeArea(
                        child: _widthHinted(
                          _isDesktopRoot
                              ? LiveDesktopShell(
                                  key: ValueKey('desktop-shell:$_rootName'),
                                  stylePreset: preset,
                                  barLayout: _barLayout,
                                  paletteOpen: _paletteOpen,
                                  onTogglePalette: () => setState(
                                    () => _paletteOpen = !_paletteOpen,
                                  ),
                                  onSelectPreset: (selected) =>
                                      unawaited(_tool('theme:${selected.id}')),
                                  onCycleBarLayout: () {
                                    setState(() {
                                      _barLayout =
                                          BarLayout.values[(_barLayout.index +
                                                  1) %
                                              BarLayout.values.length];
                                    });
                                  },
                                  onOpenCompute: () =>
                                      unawaited(_tool('compute')),
                                  profileMenu: _menu(),
                                  isGuest: _isGuestSession,
                                  guestRemaining: _guestRemaining,
                                  onSaveBrain: _triggerSaveGuestBrain,
                                  onOpenUrl: widget.onOpenUrl == null
                                      ? null
                                      : (uri) =>
                                            unawaited(widget.onOpenUrl!(uri)),
                                  downloadHubUri: _downloadHubUri,
                                  activationRouter: _activationRouter,
                                )
                              : ViewHost(
                                  key: ValueKey(
                                    '$_rootName:$_surfaceReloadEpoch',
                                  ),
                                  kind: _renderKind,
                                  name: _rootName!,
                                ),
                        ),
                      ),
                    )
                  : Center(
                      child: Text(
                        _startupError?.toString() ??
                            widget.statusMessage ??
                            'Connect to your brain to begin.',
                      ),
                    ),
              if (_departureRefusal case final String refusalMessage)
                Positioned(
                  top: 12,
                  left: 16,
                  right: 16,
                  child: SafeArea(
                    child: Center(
                      child: ConstrainedBox(
                        constraints: const BoxConstraints(maxWidth: 640),
                        child: Material(
                          key: const ValueKey(
                            'workspace-departure-refusal-banner',
                          ),
                          elevation: 4,
                          borderRadius: BorderRadius.circular(10),
                          color: Theme.of(context).colorScheme.errorContainer,
                          child: Padding(
                            padding: const EdgeInsets.symmetric(
                              horizontal: 14,
                              vertical: 10,
                            ),
                            child: Row(
                              children: [
                                Icon(
                                  Icons.warning_amber_rounded,
                                  size: 18,
                                  color: Theme.of(context)
                                      .colorScheme
                                      .onErrorContainer,
                                ),
                                const SizedBox(width: 10),
                                Expanded(
                                  child: Text(
                                    refusalMessage,
                                    style: TextStyle(
                                      color: Theme.of(context)
                                          .colorScheme
                                          .onErrorContainer,
                                      fontWeight: FontWeight.w500,
                                    ),
                                  ),
                                ),
                                IconButton(
                                  key: const ValueKey(
                                    'workspace-departure-refusal-dismiss',
                                  ),
                                  tooltip: 'Dismiss',
                                  icon: Icon(
                                    Icons.close,
                                    size: 18,
                                    color: Theme.of(context)
                                        .colorScheme
                                        .onErrorContainer,
                                  ),
                                  onPressed: () =>
                                      setState(() => _departureRefusal = null),
                                ),
                              ],
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              if (_paletteOpen)
                CommandPalette(
                  apps: _session?.launcherApps ?? const [],
                  onClose: () => setState(() => _paletteOpen = false),
                  onLaunchApp: (appId) {
                    if (appId == 'settings') {
                      unawaited(_activate(WorkspaceAppActivation.settings()));
                    } else {
                      unawaited(
                        _activate(
                          WorkspaceAppActivation(
                            application: appId,
                            operation: 'open',
                          ),
                        ),
                      );
                    }
                  },
                  onAskIno: (prompt) => unawaited(
                    _activate(
                      WorkspaceAppActivation(
                        application: 'ino',
                        operation: 'send',
                        input: prompt,
                      ),
                    ),
                  ),
                  onSelectPreset: (preset) =>
                      unawaited(_tool('theme:${preset.id}')),
                  onCycleBarLayout: () {
                    setState(() {
                      _barLayout =
                          BarLayout.values[(_barLayout.index + 1) %
                              BarLayout.values.length];
                    });
                  },
                ),
              Positioned.fill(
                child: DesktopChromeOverlayHost(
                  guestChrome: DesktopGuestChromeConfig(
                    showPill:
                        _isGuestSession &&
                        !_isDesktopRoot &&
                        widget.programmingClient?.chromeless != true,
                    remaining: _guestRemaining,
                    onSaveBrain: _triggerSaveGuestBrain,
                    onOpenUrl: widget.onOpenUrl == null
                        ? null
                        : (uri) => unawaited(widget.onOpenUrl!(uri)),
                    downloadHubUri: _downloadHubUri,
                    useSafeArea: true,
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
