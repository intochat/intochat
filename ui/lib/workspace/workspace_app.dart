import 'dart:async';
import 'dart:convert';

import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:uuid/uuid.dart';

import '../connections/connect_window.dart';
import '../desktop/command_palette.dart';
import '../desktop/guest_status_pill.dart';
import '../desktop/live_desktop_shell.dart';
import '../desktop/smart_desktop.dart';
import 'apps/app_activations.dart';
import 'cross_window_router.dart';
import 'width_hint.dart';
import 'known_apps.dart';
import 'workspace_store.dart';
import 'workspace_link.dart';
import 'compute_usage_panel.dart';
import 'workspace_preferences_io.dart'
    if (dart.library.js_interop) 'workspace_preferences_web.dart'
    as preferences;

/// Inherited controller allowing any desktop button, settings panel, or
/// studio widget to inspect and live-morph the active [UiThemeSeeds] across
/// the entire Desktop OS shell in real time.
class UiThemeSeedsController extends InheritedWidget {
  const UiThemeSeedsController({
    super.key,
    required this.seeds,
    required this.setSeeds,
    required this.resetSeeds,
    required super.child,
  });

  final UiThemeSeeds seeds;
  final ValueChanged<UiThemeSeeds> setSeeds;
  final VoidCallback resetSeeds;

  static UiThemeSeedsController? maybeOf(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<UiThemeSeedsController>();

  static UiThemeSeedsController of(BuildContext context) {
    final controller = maybeOf(context);
    assert(
      controller != null,
      'No UiThemeSeedsController found in widget tree.',
    );
    return controller!;
  }

  @override
  bool updateShouldNotify(UiThemeSeedsController oldWidget) =>
      seeds != oldWidget.seeds;
}

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
  bool _guestDownloadModalOpen = false;

  bool get _isGuestSession =>
      widget.isGuest ??
      widget.isEphemeral ??
      ((widget.programmingClient?.isGuestSession ?? false) ||
          (_brain?.startsWith('try-') ?? false));

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
  List<SmartAppTileSpec> _dynamicDesktopApps = const [];

  List<SmartAppTileSpec> _paletteApps() {
    final seen = <String>{};
    final merged = <SmartAppTileSpec>[];
    for (final s in _dynamicDesktopApps) {
      if (seen.add(s.id.toLowerCase())) merged.add(s);
    }
    for (final id in _knownApps) {
      final norm = id.trim().toLowerCase();
      if (norm.isNotEmpty && seen.add(norm)) {
        merged.add(SmartAppTileSpec.fromIconSpec(norm));
      }
    }
    return merged;
  }

  Future<void> _readKnownApps(String brain) async {
    try {
      final installed = await widget.programmingClient!.listInstalledApps(
        brain,
      );
      if (mounted && (_brain == null || _brain == brain)) {
        setState(
          () => _knownApps = knownAppsFor([
            for (final app in installed) app.id,
            for (final spec in _dynamicDesktopApps) spec.id,
          ]),
        );
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
  Future<void> activateAction(Map<String, dynamic> action) => _activate(action);
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
  Future<bool> didPushRouteInformation(
    RouteInformation routeInformation,
  ) async {
    final action = workspaceLink(routeInformation.uri);
    if (action == null) return false;
    if (!_ready) {
      _pendingLocation = routeInformation.uri;
    } else {
      await _openLink(routeInformation.uri, action);
    }
    return true;
  }

  Future<void> _openLink(Uri location, Map<String, dynamic> action) async {
    await _activate(action);
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
      if (workspaceLink(location) case final action?) {
        await _openLink(location!, action);
      }
      if (!_hasExplicitRoot &&
          widget.initialLocation?.path.startsWith('/settings') == true) {
        unawaited(_activate({'kind': 'settings'}));
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
      setState(() {
        _brain = brain;
        _rootName = '$brain/$root';
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
      onNarrow: (narrow) => unawaited(
        widget.programmingClient!
            .requestApp(brain, 'desktop', 'narrow', '$narrow')
            .then<void>((_) {}, onError: (Object _) {}),
      ),
      child: root,
    );
  }

  // The request is handled asynchronously, so connect again (a few times, briefly) until the root changes.
  Future<void> _reconnect(Map<String, dynamic> action) async {
    final brain = _brain!;
    final app = action['application'] as String?;
    final operation = action['operation'] as String?;
    if (app != null && operation != null) {
      await widget.programmingClient!.requestApp(
        brain,
        app,
        operation,
        action['input'] as String? ?? '',
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

  static String? _nonEmptyField(Object? value) {
    if (value is! String) return null;
    final trimmed = value.trim();
    return trimmed.isEmpty ? null : value;
  }

  Future<void> _activate(Map<String, dynamic> action) async {
    final client = widget.programmingClient;
    if (client == null) return;
    if (action['kind'] == 'os_uri') {
      final uriStr = '${action['uri'] ?? ''}';
      final currentScreen = screenNeuronFromViewName(
        _rootName ?? _focusedNeuron,
        brain: _brain,
      );
      final openRoute = openWindowRouteOf(
        uriStr,
        intent: action['intent'] as String?,
        fallbackFromNeuron: currentScreen,
      );
      if (openRoute != null) {
        if (openRoute.app == 'settings' && !openRoute.hasNavigationPayload) {
          action = {
            'kind': 'settings',
            if (action['brainId'] case final String b) 'brainId': b,
            if (openRoute.section case final String sec) 'tab': sec,
          };
        } else {
          action = {
            if (action['brainId'] case final String b) 'brainId': b,
            'kind': 'workspace',
            'application': openRoute.app,
            'operation': 'open',
            if (openRoute.input != null) 'input': openRoute.input,
            if (openRoute.argsJson != null) 'argsJson': openRoute.argsJson,
            if (openRoute.hasExplicitTransition)
              'transition': openRoute.transition,
            if (openRoute.fromNeuron != null)
              'fromNeuron': openRoute.fromNeuron,
            if (openRoute.hasNavigationPayload) 'hasNavigationPayload': true,
          };
        }
      } else if (uriStr.contains('settings')) {
        final uri = Uri.tryParse(uriStr);
        action = {
          'kind': 'settings',
          if (action['brainId'] case final String b) 'brainId': b,
          if (uri?.queryParameters['section'] case final String sec) 'tab': sec,
        };
      } else {
        return;
      }
    }
    // A legacy settings activation is the Settings app's workspace activation.
    final isLegacySettings = action['kind'] == 'settings';
    if (isLegacySettings) action = settingsActivation(action);
    // An app's configuration is the Apps app's detail for it.
    if (action['view'] == 'configuration' && action['application'] is String) {
      action = configurationActivation(action);
    }
    try {
      if (action['kind'] == 'account') {
        await widget.onSwitchAccount?.call();
        return;
      }
      // The lobby neuron signed the visitor in: leave the lobby and read the member session.
      if (action['kind'] == 'lobby-ended') {
        await client.endLobby();
        return;
      }
      // The layout switch: hand the app its request, then connect again and mount the new root.
      // Only the desktop's layout switch may reconnect; any other reconnect is ignored.
      if (action['kind'] == 'reconnect') {
        if (action['application'] == 'desktop' && _brain != null) {
          await _reconnect(action);
        }
        return;
      }
      final brain = action['brainId'] as String? ?? _brain!;
      final app = action['application'] as String?;
      final neuron =
          action['neuron'] as String? ??
          (app == null ? null : '$brain/apps/$app');
      final changingBrain = brain != _brain;
      final navInput = _nonEmptyField(action['input']);
      final navArgsJson = _nonEmptyField(action['argsJson'] ?? action['args']);
      final rawTransition = _nonEmptyField(action['transition']);
      final navTransition = normalizeViewTransition(rawTransition);
      final fromNeuron = _nonEmptyField(action['fromNeuron']);
      final hasNavPayload =
          action['hasNavigationPayload'] == true ||
          navInput != null ||
          navArgsJson != null ||
          rawTransition != null;
      final alreadyNavigatedTo = action['navigatedTo'] == true;
      // Install, update and remove run over the person's own session in a brain they belong to; the space
      // stays where it is, and an install then opens the app.
      if (const {'install', 'update', 'remove'}.contains(action['kind'])) {
        if (changingBrain &&
            !(await client.listBrains()).any((entry) => entry.id == brain)) {
          throw StateError('This account is not a member of that brain.');
        }
        final result = await runAppActivation({
          ...action,
          'brainId': brain,
        }, client.jsonRequest);
        if (!mounted) return;
        if (!changingBrain) unawaited(_readKnownApps(brain));
        if (result.toast.isNotEmpty) _toast(result.toast, tone: result.tone);
        if (result.reopen case final String reopen) {
          await _activate({
            'brainId': brain,
            'kind': 'workspace',
            'application': 'apps',
            'operation': 'open',
            'input': reopen,
          });
        }
        if (result.open case final String open) {
          await _activate({
            'brainId': brain,
            'kind': 'workspace',
            'application': open,
            'operation': 'open',
          });
        }
        return;
      }
      if (changingBrain) {
        final brains = await client.listBrains();
        if (!brains.any((entry) => entry.id == brain)) {
          throw StateError('This account is not a member of that brain.');
        }
        if (!mounted) return;
        await _selectBrain(
          brain,
          show:
              const {
                'connections',
                'install',
                'update',
                'remove',
              }.contains(action['kind'])
              ? null
              : neuron,
        );
      } else if (app != null && app.trim().isNotEmpty) {
        final normApp = app.trim().toLowerCase();
        if (!_knownApps.contains(normApp)) {
          setState(() => _knownApps = {..._knownApps, normApp});
        }
      }
      if (action['kind'] == 'connections') {
        if (!mounted) return;
        showDialog<void>(
          context: _navigator.currentContext!,
          builder: (_) => Dialog(
            child: SizedBox(
              width: 560,
              height: 520,
              child: ConnectWindow(
                // Connect preselects its connector; the connection is the one the activation names, else the connector.
                initialConnectorId: action['connector'] as String?,
                initialConnectionId: connectWindowConnectionId(action),
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
        return;
      }
      if (_hasExplicitRoot) {
        final receiver = _activationApp;
        if (receiver == null && !_isDesktopRoot) {
          if (app == null || neuron == null) {
            throw StateError('This root has no navigation receiver.');
          }
          final currentScreen = screenNeuronFromViewName(
            _rootName,
            brain: brain,
          );
          if (!changingBrain &&
              currentScreen != null &&
              currentScreen != neuron) {
            final refusal = await checkDepartureGuard(
              sendAct: (event) => client.uiEvent(brain, event),
              fromNeuron: currentScreen,
              toNeuron: neuron,
              route: navArgsJson ?? navInput,
            );
            if (refusal != null) {
              if (mounted) {
                setState(() {
                  _departureRefusal = refusal;
                  _surfaceReloadEpoch++;
                });
                _toast(refusal, tone: UiTone.error);
              }
              return;
            }
          }
          var navDelivered = alreadyNavigatedTo;
          if (!isLegacySettings && hasNavPayload && !alreadyNavigatedTo) {
            navDelivered = await dispatchNavigateTo(
              sendAct: (event) => client.uiEvent(brain, event),
              targetNeuron: neuron,
              fromNeuron: fromNeuron ?? currentScreen,
              argsJson: navArgsJson,
              transition: navTransition,
              input: navInput,
            );
          }
          if (action['operation'] case final String operation
              when !navDelivered) {
            unawaited(
              client
                  .requestApp(
                    brain,
                    app,
                    operation,
                    action['input'] as String? ?? '',
                  )
                  .then<void>((_) {}, onError: (Object _) {}),
            );
          }
          if (mounted) {
            setState(() {
              _departureRefusal = null;
              _rootName = '$brain/apps/$app/view';
              _renderKind = 'view';
              _focusedNeuron = neuron;
              _surfaceReloadEpoch++;
            });
          }
          return;
        }
        if (receiver != null) {
          await client.requestApp(
            brain,
            receiver,
            'activate',
            jsonEncode(action),
          );
        }
        if (_isDesktopRoot) {
          var navDelivered = alreadyNavigatedTo;
          if (neuron != null &&
              !isLegacySettings &&
              hasNavPayload &&
              !alreadyNavigatedTo) {
            navDelivered = await dispatchNavigateTo(
              sendAct: (event) => client.uiEvent(brain, event),
              targetNeuron: neuron,
              fromNeuron: fromNeuron,
              argsJson: navArgsJson,
              transition: navTransition,
              input: navInput,
            );
          }
          if (action['operation'] case final String operation
              when !navDelivered && app != null && app != receiver) {
            await client.requestApp(
              brain,
              app,
              operation,
              action['input'] as String? ?? '',
            );
          }
        }
        return;
      }
      final previousScreen = screenNeuronFromViewName(
        _focusedNeuron,
        brain: brain,
      );
      if (!_isDesktopRoot &&
          neuron != null &&
          !changingBrain &&
          previousScreen != null &&
          previousScreen != neuron) {
        final refusal = await checkDepartureGuard(
          sendAct: (event) => client.uiEvent(brain, event),
          fromNeuron: previousScreen,
          toNeuron: neuron,
          route: navArgsJson ?? navInput,
        );
        if (refusal != null) {
          if (mounted) {
            setState(() {
              _departureRefusal = refusal;
              _surfaceReloadEpoch++;
            });
            _toast(refusal, tone: UiTone.error);
          }
          return;
        }
      }
      if (neuron != null && !changingBrain) {
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
      }
      var navDelivered = alreadyNavigatedTo;
      if (neuron != null &&
          !isLegacySettings &&
          hasNavPayload &&
          !alreadyNavigatedTo) {
        navDelivered = await dispatchNavigateTo(
          sendAct: (event) => client.uiEvent(brain, event),
          targetNeuron: neuron,
          fromNeuron: fromNeuron ?? previousScreen,
          argsJson: navArgsJson,
          transition: navTransition,
          input: navInput,
        );
      }
      if (action['operation'] case final String operation when !navDelivered) {
        if (app != null) {
          await client.requestApp(
            brain,
            app,
            operation,
            action['input'] as String? ?? '',
          );
        }
      }
    } on StateError catch (error) {
      // Shell-authored copy (e.g. membership), never a transport exception.
      if (mounted) _toast(error.message, tone: UiTone.error);
    } catch (_) {
      if (mounted) {
        _toast('Could not navigate there. Try again.', tone: UiTone.error);
      }
    }
  }

  Future<void> _tool(String name) async {
    final client = widget.programmingClient!;
    final navigator = _navigator.currentState!;
    try {
      switch (name) {
        case 'preferences':
          await _activate({'kind': 'settings'});
        case 'connections':
          await _activate({'kind': 'connections'});
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
          await _activate({
            'kind': 'workspace',
            'application': 'gallery',
            'operation': 'open',
          });
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
                      _activate({
                        'kind': 'workspace',
                        'application': appId,
                        'operation': 'open',
                      }),
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
        case 'switch':
          await widget.onSwitchAccount?.call();
      }
    } catch (_) {
      if (mounted) {
        _toast('Could not open that tool. Try again.', tone: UiTone.error);
      }
    }
  }

  IcStylePreset _currentStylePreset() => switch (store.settings.theme) {
    'fluent-acrylic' => IcStylePreset.fluentAcrylic,
    'spatial-glass' => IcStylePreset.spatialGlass,
    'cyber-synaptic' => IcStylePreset.cyberSynaptic,
    'warm-paper' || 'intochat' || 'paper' || 'light' => IcStylePreset.warmPaper,
    'fluent-warm-mica' => IcStylePreset.fluentWarmMica,
    'linear-carbon' => IcStylePreset.linearCarbon,
    'nordic-tactical' => IcStylePreset.nordicTactical,
    _ => IcStylePreset.warmPaper,
  };

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
    store.removeListener(_changed);
    if (widget.store == null) store.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final preset = _currentStylePreset();
    final effectiveTheme = _effectiveTheme;
    return MaterialApp(
      navigatorKey: _navigator,
      debugShowCheckedModeBanner: false,
      theme: UiTheme.light(effectiveTheme),
      darkTheme: UiTheme.dark(effectiveTheme),
      themeMode: UiTheme.modeFor(effectiveTheme),
      builder: (context, child) {
        final brightness = Theme.of(context).brightness;
        final activeSeeds = _hasCustomThemeSeeds
            ? _themeSeeds
            : (brightness == Brightness.dark
                  ? UiThemeSeeds.warmDark
                  : UiThemeSeeds.warmPaper);
        return UiThemeSeedsController(
          seeds: activeSeeds,
          setSeeds: setThemeSeeds,
          resetSeeds: resetThemeSeeds,
          child: UiSeedThemeScope(
            seeds: activeSeeds,
            child: IcTheme(
              colors: IcColors.forPreset(
                preset,
                dark: brightness == Brightness.dark,
              ),
              child: MediaQuery(
                data: MediaQuery.of(context).copyWith(
                  disableAnimations:
                      store.settings.reducedMotion ||
                      MediaQuery.disableAnimationsOf(context),
                ),
                child: child!,
              ),
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
              _rootName != null && _brain != null
                  ? SafeArea(
                      child: _widthHinted(
                        _isDesktopRoot
                            ? LiveDesktopShell(
                                key: ValueKey('desktop-shell:$_rootName'),
                                brain: _brain!,
                                name: _rootName!,
                                load: (kind, name) =>
                                    widget.programmingClient!.appRequest(
                                      _brain!,
                                      appNodePath(kind, name),
                                    ),
                                watch: (kind, name) => widget.programmingClient!
                                    .watchNode(_brain!, kind, name)
                                    .map((_) {}),
                                watchSignal: (contract, key, signal) => widget
                                    .programmingClient!
                                    .watches(_brain!)
                                    .watch(
                                      WatchSourceSpec(
                                        contract,
                                        key.startsWith('$_brain/')
                                            ? key
                                            : '$_brain/$key',
                                        [signal],
                                      ),
                                    )
                                    .map((s) => s.payload),
                                onAction: (event) => widget.programmingClient!
                                    .uiEvent(_brain!, event),
                                knowsApp: (app) =>
                                    _knownApps.contains(app) ||
                                    _knownApps.contains(app.toLowerCase()),
                                installedApps: _knownApps.toList(),
                                onDynamicAppsChanged: (extras) {
                                  if (!mounted) return;
                                  setState(() {
                                    _dynamicDesktopApps = extras;
                                    for (final spec in extras) {
                                      _knownApps = {
                                        ..._knownApps,
                                        spec.id.toLowerCase(),
                                      };
                                    }
                                  });
                                },
                                requestApp: (app, operation, input) => widget
                                    .programmingClient!
                                    .requestApp(_brain!, app, operation, input),
                                onActivate: (action) =>
                                    unawaited(_activate(action)),
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
                                scenes: widget.programmingClient!.scenes(
                                  _brain!,
                                ),
                                transcribe:
                                    widget.programmingClient!.transcribe,
                                talk: widget.programmingClient!.talk,
                                speak: widget.programmingClient!.speak,
                                storeSecret: widget
                                    .programmingClient!
                                    .storeSecretReference,
                                journal: (brain, since) => widget
                                    .programmingClient!
                                    .watchJournal(brain, since: since),
                                entries: (brain) => EntriesClient(
                                  widget.programmingClient!,
                                  brain,
                                ),
                                localPreferences: _localPreferences,
                                profileMenu: _menu(),
                                read: (contract, key, method) => widget
                                    .programmingClient!
                                    .channel
                                    .call(_brain!, contract, key, method, []),
                                callNeuron: (contract, key, method, args) =>
                                    widget.programmingClient!.channel.call(
                                      _brain!,
                                      contract,
                                      key.startsWith('$_brain/')
                                          ? key
                                          : '$_brain/$key',
                                      method,
                                      args,
                                    ),
                                isGuest: _isGuestSession,
                                guestRemaining: _guestRemaining,
                                onSaveBrain: _triggerSaveGuestBrain,
                                onOpenUrl: widget.onOpenUrl == null
                                    ? null
                                    : (uri) =>
                                          unawaited(widget.onOpenUrl!(uri)),
                                downloadHubUri: _downloadHubUri,
                              )
                            : ViewHost(
                                key: ValueKey(
                                  '$_rootName:$_surfaceReloadEpoch',
                                ),
                                kind: _renderKind,
                                name: _rootName!,
                                load: (kind, name) =>
                                    widget.programmingClient!.appRequest(
                                      _brain!,
                                      appNodePath(kind, name),
                                    ),
                                // Reads once per change signal instead of every 2 s.
                                watch: (kind, name) => widget.programmingClient!
                                    .watchNode(_brain!, kind, name)
                                    .map((_) {}),
                                watchSignal: (contract, key, signal) => widget
                                    .programmingClient!
                                    .watches(_brain!)
                                    .watch(
                                      WatchSourceSpec(
                                        contract,
                                        key.startsWith('$_brain/')
                                            ? key
                                            : '$_brain/$key',
                                        [signal],
                                      ),
                                    )
                                    .map((s) => s.payload),
                                callNeuron: (contract, key, method, args) =>
                                    widget.programmingClient!.channel.call(
                                      _brain!,
                                      contract,
                                      key.startsWith('$_brain/')
                                          ? key
                                          : '$_brain/$key',
                                      method,
                                      args,
                                    ),
                                onAction: (event) => widget.programmingClient!
                                    .uiEvent(_brain!, event),
                                onActivate: (action) =>
                                    unawaited(_activate(action)),
                                transcribe:
                                    widget.programmingClient!.transcribe,
                                talk: widget.programmingClient!.talk,
                                speak: widget.programmingClient!.speak,
                                storeSecret: widget
                                    .programmingClient!
                                    .storeSecretReference,
                                knowsApp: (app) =>
                                    _knownApps.contains(app) ||
                                    _knownApps.contains(app.toLowerCase()),
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
                  apps: _paletteApps(),
                  onClose: () => setState(() => _paletteOpen = false),
                  onLaunchApp: (appId) {
                    if (appId == 'settings') {
                      unawaited(_activate({'kind': 'settings'}));
                    } else {
                      unawaited(
                        _activate({
                          'kind': 'workspace',
                          'application': appId,
                          'operation': 'open',
                        }),
                      );
                    }
                  },
                  onAskIno: (prompt) => unawaited(
                    _activate({
                      'kind': 'workspace',
                      'application': 'ino',
                      'operation': 'send',
                      'input': prompt,
                    }),
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
              if (_isGuestSession &&
                  !_isDesktopRoot &&
                  widget.programmingClient?.chromeless != true)
                Positioned(
                  top: 10,
                  left: 16,
                  right: 16,
                  child: SafeArea(
                    child: Align(
                      alignment: Alignment.topCenter,
                      child: GuestStatusPill(
                        remaining: _guestRemaining,
                        onSaveBrain: _triggerSaveGuestBrain,
                        onDownloadApp: () =>
                            setState(() => _guestDownloadModalOpen = true),
                      ),
                    ),
                  ),
                ),
              if (_guestDownloadModalOpen)
                Positioned.fill(
                  child: DownloadAppModal(
                    onClose: () =>
                        setState(() => _guestDownloadModalOpen = false),
                    onOpenUrl: widget.onOpenUrl == null
                        ? null
                        : (uri) => unawaited(widget.onOpenUrl!(uri)),
                    downloadHubUri: _downloadHubUri,
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// A legacy `{kind: settings, brainId?, tab?}` (an older Ino card or Desktop button) as the Settings app's
/// workspace activation: open its window in that brain and ask it for the tab (the app maps an old or no name).
Map<String, dynamic> settingsActivation(Map<String, dynamic> action) => {
  'kind': 'workspace',
  if (action['brainId'] case final String brain) 'brainId': brain,
  'application': 'settings',
  'operation': 'open',
  'input': action['tab'] is String ? action['tab'] as String : '',
};
