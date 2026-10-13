import 'dart:async';
import 'dart:typed_data';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';

import '../connections/connect_window.dart';
import '../connections/connectors_menu.dart';
import '../space/neuron_space.dart';
import '../space/space_controller.dart';
import 'home/app_detail_screen.dart';
import 'workspace_store.dart';
import 'workspace_settings.dart';
import 'compute_usage_panel.dart';
import 'csharp/csharp_manager.dart';
import 'workspace_preferences_io.dart'
    if (dart.library.js_interop) 'workspace_preferences_web.dart'
    as preferences;

class WorkspaceApp extends StatefulWidget {
  const WorkspaceApp({
    super.key,
    this.store,
    this.onSwitchAccount,
    this.initialLocation,
    this.persistenceKey = 'digitalbrain.preferences.v1',
    this.onOpenUrl,
    this.kernelBaseUri,
    this.statusMessage,
    this.programmingClient,
  });
  final WorkspaceStore? store;
  final Future<void> Function()? onSwitchAccount;
  final Uri? initialLocation, kernelBaseUri;
  final String persistenceKey;
  final OpenUrl? onOpenUrl;
  final String? statusMessage;
  final DigitalBrainUiClient? programmingClient;
  @override
  State<WorkspaceApp> createState() => _WorkspaceAppState();
}

class _WorkspaceAppState extends State<WorkspaceApp> {
  late final WorkspaceStore store =
      widget.store ??
      WorkspaceStore(
        readPreferences: () =>
            preferences.readPreferences(widget.persistenceKey),
        writePreferences: (value) =>
            preferences.writePreferences(widget.persistenceKey, value),
      );
  final _navigator = GlobalKey<NavigatorState>();
  final _messenger = GlobalKey<ScaffoldMessengerState>();
  SpaceController? _space;
  String? _brain;
  Object? _startupError;
  bool _developerMode = false;
  @override
  void initState() {
    super.initState();
    store.addListener(_changed);
    unawaited(_start());
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
      unawaited(
        client
            .readCapabilities()
            .then((value) {
              if (mounted) setState(() => _developerMode = value.developerMode);
            })
            .catchError((Object _) {}),
      );
      if (widget.initialLocation?.path.startsWith('/settings') == true) {
        await _space!.show('$brain/apps/settings');
      }
    } catch (error) {
      if (mounted) setState(() => _startupError = error);
    }
  }

  Future<void> _selectBrain(String brain, {String? show}) async {
    if (_brain == brain) return;
    final client = widget.programmingClient!;
    final old = _space;
    final controller = SpaceController(
      client: client,
      scenes: client.scenes(brain),
      brain: brain,
    );
    // Initial connection exposes its read error and retry on the space screen.
    if (old == null) {
      _brain = brain;
      _space = controller;
      if (mounted) setState(() {});
      await controller.start();
      return;
    }
    try {
      await controller.start();
      if (controller.error.value case final Object problem) throw problem;
      if (show != null) await controller.show(show);
      if (!mounted) {
        controller.dispose();
        return;
      }
      setState(() {
        _brain = brain;
        _space = controller;
      });
      old.dispose();
    } catch (_) {
      controller.dispose();
      rethrow;
    }
  }

  Future<Uint8List?> _icon(String neuron, String icon) async {
    final client = widget.programmingClient;
    if (client == null) return null;
    final marker = neuron.indexOf('/apps/');
    if (marker <= 0) return null;
    final brain = neuron.substring(0, marker);
    final app = neuron.substring(marker + 6).split('/').first;
    final manifest = await client.readAppManifest(brain, app);
    if (manifest.icon != icon || shellIcon(icon) != null) return null;
    return client.readBytes(
      '/brains/${Uri.encodeComponent(brain)}/apps/${Uri.encodeComponent(app)}/icon',
    );
  }

  Future<void> _activate(Map<String, dynamic> action) async {
    final client = widget.programmingClient;
    if (client == null) return;
    try {
      final brain = action['brainId'] as String? ?? _brain!;
      final app = action['application'] as String?;
      final neuron =
          action['neuron'] as String? ??
          (app == null ? null : '$brain/apps/$app');
      final changingBrain = brain != _brain;
      if (changingBrain) {
        final brains = await client.listBrains();
        if (!brains.any((entry) => entry.id == brain)) {
          throw StateError('This account is not a member of that brain.');
        }
        if (!mounted) return;
        await _selectBrain(
          brain,
          show:
              action['view'] == 'configuration' ||
                  action['kind'] == 'connections'
              ? null
              : neuron,
        );
      }
      if (action['view'] == 'configuration' && app != null) {
        if (!mounted) return;
        _navigator.currentState?.push(
          MaterialPageRoute<void>(
            builder: (_) => AppDetailScreen(
              workspaceId: brain,
              installedName: app,
              request: client.jsonRequest,
              onOpen: (name) =>
                  _activate({'brainId': brain, 'application': name}),
              loadIcon: client.readBytes,
            ),
          ),
        );
        return;
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
      if (neuron != null && !changingBrain) await _space!.show(neuron);
      if (action['operation'] case final String operation) {
        if (app != null) {
          await client.requestApp(
            brain,
            app,
            operation,
            action['input'] as String? ?? '',
          );
        }
      }
    } catch (error) {
      if (mounted) {
        _messenger.currentState?.showSnackBar(
          SnackBar(content: Text('Could not navigate: $error')),
        );
      }
    }
  }

  Future<void> _request(String operation, String input) async {
    final client = widget.programmingClient, space = _space;
    if (client == null || space == null) return;
    final prefix = '${space.brain}/apps/';
    final focused = space.view.value.focused;
    if (!focused.startsWith(prefix)) return;
    try {
      await client.requestApp(
        space.brain,
        focused.substring(prefix.length).split('/').first,
        operation,
        input,
      );
    } catch (error) {
      if (mounted) {
        _messenger.currentState?.showSnackBar(
          SnackBar(content: Text('Could not run action: $error')),
        );
      }
    }
  }

  Widget _settings(String section) => WorkspaceSettings(
    store: store,
    initialSection: section,
    onClose: () => _navigator.currentState?.pop(),
    kernelBaseUri: widget.kernelBaseUri,
    onOpen: widget.onOpenUrl,
    connectionsRequest: (path, {body}) => widget.programmingClient!
        .workspaceConnectionsRequest(_brain!, path, body: body),
    connectorRequest: (path, {body}) =>
        widget.programmingClient!.connectorRequest(_brain!, path, body: body),
    windowsRequest: (path, {body}) => widget.programmingClient!
        .workspaceWindowsRequest(_brain!, path, body: body),
    loadModels: widget.programmingClient!.readModelCatalog,
    sectionBuilder: _settings,
  );
  Future<void> _tool(String name) async {
    final client = widget.programmingClient!;
    final navigator = _navigator.currentState!;
    try {
      switch (name) {
        case 'preferences':
          navigator.push(
            MaterialPageRoute<void>(
              settings: const RouteSettings(name: '/settings/general'),
              builder: (_) => _settings('general'),
            ),
          );
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
        case 'csharp':
          if (_developerMode) {
            navigator.push(
              MaterialPageRoute<void>(
                builder: (_) => Scaffold(
                  appBar: AppBar(title: const Text('C# files')),
                  body: CSharpManager(
                    request: (method, path, {body}) =>
                        client.csharpRequest(_brain!, method, path, body: body),
                    onAsk: (id, prompt) => unawaited(
                      client.requestApp(_brain!, 'ino', 'ask', prompt),
                    ),
                    onSelected: (_) {},
                  ),
                ),
              ),
            );
          }
        case 'new':
          final brain = await client.createWorkspace();
          if (mounted) await _selectBrain(brain);
        case 'switch':
          await widget.onSwitchAccount?.call();
      }
    } catch (error) {
      if (mounted) {
        _messenger.currentState?.showSnackBar(
          SnackBar(content: Text('Could not open tool: $error')),
        );
      }
    }
  }

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
      if (_developerMode)
        const PopupMenuItem(value: 'csharp', child: Text('C# files')),
      const PopupMenuItem(value: 'new', child: Text('New workspace')),
      if (widget.onSwitchAccount != null)
        const PopupMenuItem(value: 'switch', child: Text('Switch account')),
    ],
  );
  @override
  void dispose() {
    _space?.dispose();
    store.removeListener(_changed);
    if (widget.store == null) store.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => MaterialApp(
    navigatorKey: _navigator,
    scaffoldMessengerKey: _messenger,
    debugShowCheckedModeBanner: false,
    theme: UiTheme.workspace(Brightness.light),
    darkTheme: UiTheme.workspaceDark(store.settings.theme),
    themeMode: UiTheme.modeFor(store.settings.theme),
    builder: (context, child) => MediaQuery(
      data: MediaQuery.of(context).copyWith(
        disableAnimations:
            store.settings.reducedMotion ||
            MediaQuery.disableAnimationsOf(context),
      ),
      child: child!,
    ),
    home: Scaffold(
      body: _space == null
          ? Center(
              child: Text(
                _startupError?.toString() ??
                    widget.statusMessage ??
                    'Connect to your brain to begin.',
              ),
            )
          : NeuronSpace(
              key: ValueKey(_brain),
              controller: _space!,
              load: (kind, name) => widget.programmingClient!.appRequest(
                _brain!,
                'node?${Uri(queryParameters: {'kind': kind, 'name': name}).query}',
              ),
              watch: (kind, name) => widget.programmingClient!
                  .watchNode(_brain!, kind, name)
                  .map((_) {}),
              onAction: (event) async {
                await widget.programmingClient!.appRequest(
                  _brain!,
                  'event',
                  body: event,
                );
              },
              onActivate: (action) => unawaited(_activate(action)),
              onRequest: _request,
              loadIcon: _icon,
              menu: _menu(),
              onSettings: () => unawaited(
                _space!.show('$_brain/apps/settings').catchError((Object _) {}),
              ),
            ),
    ),
  );
}
