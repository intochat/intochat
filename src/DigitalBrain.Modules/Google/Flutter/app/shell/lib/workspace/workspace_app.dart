import 'dart:async';
import 'dart:ui' show SemanticsRole;

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';

import '../connections/connect_window.dart';
import '../connections/connectors_menu.dart';
import 'workspace_store.dart';
import 'neuron_workspace_persistence.dart';
import 'workspace_remote_controller.dart';
import 'workspace_desktop.dart';
import 'workspace_settings.dart';
import 'workspace_routes.dart';

import 'compute_usage_panel.dart';
import 'workspace_islands.dart';
import 'csharp/csharp_manager.dart';
import 'home/home_screen.dart';
import 'home/circle_activity_badge.dart';
import 'dock/dock.dart';
import 'home/infrastructure_strip.dart';

class WorkspaceApp extends StatefulWidget {
  const WorkspaceApp({
    super.key,
    this.store,
    this.onSwitchAccount,
    this.initialLocation,
    this.persistenceKey = 'intocaht.workspace.v1',
    this.onOpenUrl,
    this.kernelBaseUri,
    this.statusMessage,
    this.onReadTable,
    this.onUpdateTableView,
    this.onListTables,
    this.programmingClient,
    this.connectedSources = const [],
  });
  final WorkspaceStore? store;
  final Future<void> Function()? onSwitchAccount;
  final Uri? initialLocation;
  final String persistenceKey;
  final OpenUrl? onOpenUrl;
  final Uri? kernelBaseUri;
  final String? statusMessage;
  final ReadTable? onReadTable;
  final UpdateTableView? onUpdateTableView;
  final ListTables? onListTables;
  final DigitalBrainUiClient? programmingClient;

  /// Sources the shell knows are connected; pushed to the workspace so first-run prompts match.
  final List<String> connectedSources;
  @override
  State<WorkspaceApp> createState() => _WorkspaceAppState();
}

class _WorkspaceAppState extends State<WorkspaceApp> {
  late final WorkspaceStore store =
      widget.store ??
      WorkspaceStore(
        seedProject: false,
        persistence: widget.programmingClient == null
            ? MemoryWorkspacePersistence()
            : NeuronWorkspacePersistence(client: widget.programmingClient!),
      );
  final _tables = <String, UiTableController>{};
  final _remote = <String, WorkspaceRemoteController>{};
  final _remoteCancelled = Completer<void>();
  final _apps = <String, List<InstalledAppSummary>>{};
  final _appsLoading = <String>{};
  final _sourcesSynced = <String>{};
  final _circleFeeds = <String, CircleActivityFeed>{};
  final _computeRefresh = ValueNotifier<int>(0);
  final _presentationConfigured = <String>{};
  final _scenes = <String, SceneClient>{};
  final _brainWatches = <String, WatchBrain>{};
  bool _companionExpanded = true;
  bool _animateFocus = false;

  void _connectWorkspaces() {
    final client = widget.programmingClient;
    if (client == null || !_ready) return;
    store.onRemoteFocus = (workspace, window) =>
        unawaited(_remoteFocus(workspace, window));
    store.onRemoteWindowAction = (workspace, window, open) =>
        unawaited(_remoteAction(workspace, window, open));
    for (final project in store.projects) {
      if (_remote.containsKey(project.id)) continue;
      if (_sourcesSynced.add(project.id)) {
        unawaited(
          client
              .setConnectedSources(project.id, widget.connectedSources)
              .then((snapshot) {
                if (mounted) store.reconcileWorkspace(project, snapshot);
              })
              .catchError((Object _) {}),
        );
      }
      final controller = WorkspaceRemoteController(
        read: (id) =>
            client.readWorkspace(id, cancelled: _remoteCancelled.future),
        watch: client.grpcLive
            ? client.watchWorkspaceChannel
            : client.watchWorkspace,
        apply: (state) {
          if (mounted) {
            _animateFocus = false;
            store.reconcileWorkspace(project, state);
            unawaited(_configurePresentation(project, state));
          }
        },
        onError: (error) {
          if (mounted) {
            _messenger.currentState?.showSnackBar(
              const SnackBar(
                content: Text(
                  'Workspace connection interrupted. Reconnecting…',
                ),
              ),
            );
          }
        },
      );
      _remote[project.id] = controller;
      controller.start(project.id);
    }
  }

  Future<void> _remoteAction(String workspace, String window, bool open) async {
    final client = widget.programmingClient;
    if (client == null) return;
    try {
      final revision = store.remoteRevisions[workspace] ?? 0;
      final state = open
          ? await client.reopenWorkspaceWindow(workspace, window, revision)
          : await client.closeWorkspaceWindow(workspace, window, revision);
      if (!mounted) return;
      final project = store.projects.firstWhere((p) => p.id == workspace);
      store.reconcileWorkspace(
        project,
        state,
        openedWindowId: open ? window : null,
      );
    } catch (error) {
      await _remote[workspace]?.refresh();
      if (mounted) {
        _messenger.currentState?.showSnackBar(
          SnackBar(content: Text('Window changed. Please try again. $error')),
        );
      }
    }
  }

  final _hydratingTables = <String>{};
  final _tableErrors = <String, String>{};

  final _messenger = GlobalKey<ScaffoldMessengerState>();
  final _navigator = GlobalKey<NavigatorState>();
  late final WorkspaceRouteBridge _routes;
  late final Uri _initialLocation;
  bool _ready = false, _directory = false;
  // Null follows the desktop: Home when nothing is open, the desktop when windows are.
  // Opening something or the Home island makes it an explicit choice.
  bool? _homeChoice;
  bool get _home => widget.programmingClient != null
      ? store.focusedWindow == null
      : _homeChoice ??
            store.currentProject.presentation.openArtifactIds.isEmpty;
  String _query = '';
  @override
  void initState() {
    super.initState();
    _initialLocation =
        widget.initialLocation ??
        Uri.parse(WidgetsBinding.instance.platformDispatcher.defaultRouteName);
    _routes = WorkspaceRouteBridge(onNavigate: _navigateRoute);
    store.addListener(_changed);
    _load();
  }

  Future<void> _load() async {
    await store.load();
    if (!mounted) return;
    if (store.loadFailed) {
      setState(() {});
      return;
    }
    // The server owns the brain: the session's default brain is always a project, and a
    // project id is never invented here while a server is connected.
    final owned = widget.programmingClient?.defaultBrainId;
    if (owned == null) {
      if (store.projects.isEmpty) store.createProject('Personal');
    } else if (!store.projects.any((project) => project.id == owned)) {
      final conversation = WorkspaceConversation(
        id: 'initial',
        title: 'New conversation',
      );
      store.projects.insert(
        0,
        WorkspaceProject(
          id: owned,
          title: 'Personal',
          conversations: [conversation],
          selectedConversationId: conversation.id,
        ),
      );
      store.selectedProjectId = owned;
      store.save();
    }
    if (!mounted) return;
    setState(() => _ready = true);
    _connectWorkspaces();
    _loadApps(store.currentProject.id);
    _loadCapabilities();
    _navigateRoute(_initialLocation);
  }

  int _reloadGeneration = 0;
  Future<void> _reloadSavedState(BuildContext context) async {
    final discard = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Reload saved state?'),
        content: const Text(
          'Unsaved changes in this session will be discarded. Copy any work you want to keep before reloading. The original device copy will be retained; its import will be skipped for this session.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Keep my changes'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Discard and reload'),
          ),
        ],
      ),
    );
    if (discard != true || !mounted) return;
    for (final controller in _remote.values) {
      controller.dispose();
    }
    _remote.clear();
    _sourcesSynced.clear();
    for (final table in _tables.values) {
      table.dispose();
    }
    _tables.clear();
    setState(() {
      _ready = false;
      _reloadGeneration++;
    });
    await store.reload();
    if (mounted) await _load();
  }

  void _loadCapabilities() {
    final client = widget.programmingClient;
    if (client == null) return;
    unawaited(
      client
          .readCapabilities()
          .then((capabilities) {
            if (mounted) store.developerMode = capabilities.developerMode;
          })
          .catchError((Object _) {}),
    );
  }

  void _navigateRoute(Uri uri) {
    if (!_ready || !mounted) return;
    final segments = uri.pathSegments;
    _navigator.currentState?.popUntil((route) => route.isFirst);
    if (segments.firstOrNull == 'settings') {
      if (segments.length == 1 && widget.programmingClient != null) {
        unawaited(_openApplication('settings'));
        return;
      }
      void showSettings() {
        if (!mounted) return;
        final section = segments.length > 2 && segments[2] == 'gallery'
            ? 'ui-kit'
            : segments.length > 1
            ? segments[1]
            : 'general';
        _navigator.currentState?.push(
          MaterialPageRoute<void>(
            settings: RouteSettings(name: '/settings/$section'),
            builder: (_) => _settingsApp(
              () => _navigator.currentState?.pop(),
              section: section,
            ),
          ),
        );
      }

      if (_navigator.currentState != null) {
        showSettings();
      } else {
        WidgetsBinding.instance.addPostFrameCallback((_) => showSettings());
      }
      return;
    }
    setState(() {
      final id = segments.length > 1 ? segments[1] : null;
      if (segments.firstOrNull == 'projects' &&
          id != null &&
          store.projects.any((project) => project.id == id)) {
        store.selectProject(id);
        _directory = false;
      } else {
        _directory = false;
      }
    });
  }

  String? _lastSelectedProject;

  void _changed() {
    _connectWorkspaces();
    // A switch re-reads the workspace authoritatively, so a window opened remotely while this
    // workspace was in the background appears even if its change event was missed.
    if (store.selectedProjectId != _lastSelectedProject) {
      _lastSelectedProject = store.selectedProjectId;
      final controller = _remote[_lastSelectedProject];
      if (controller != null) unawaited(controller.refresh());
    }
    if (store.projects.isNotEmpty) _loadApps(store.currentProject.id);
    if (mounted) setState(() {});
  }

  void _loadApps(String workspaceId) {
    final client = widget.programmingClient;
    if (client == null ||
        _apps.containsKey(workspaceId) ||
        !_appsLoading.add(workspaceId)) {
      return;
    }
    unawaited(
      client
          .listInstalledApps(workspaceId)
          .then((apps) {
            if (!mounted) return;
            _apps[workspaceId] = apps;
            setState(() {});
          })
          .catchError((Object _) {
            _appsLoading.remove(workspaceId);
          }),
    );
  }

  @override
  void dispose() {
    _computeRefresh.dispose();
    for (final feed in _circleFeeds.values) {
      feed.dispose();
    }
    _remoteCancelled.complete();
    for (final controller in _remote.values) {
      controller.dispose();
    }
    store.onRemoteWindowAction = null;
    store.onRemoteFocus = null;
    _routes.dispose();
    store.removeListener(_changed);
    for (final t in _tables.values) {
      t.dispose();
    }
    if (widget.store == null) store.dispose();
    super.dispose();
  }

  void _accept(
    Map<String, dynamic> result, {
    WorkspaceProject? project,
    bool reveal = true,
  }) {
    final destination = project ?? store.currentProject;
    if (result['remoteManaged'] == true) {
      final window = result['windowId'] as String?;
      if (window != null) {
        unawaited(_remoteAction(destination.id, window, true));
      }
      return;
    }
    final id = result['id'] as String?;
    if (id == null) return;
    if (result['kind'] == 'table') {
      try {
        final snapshot = TableSnapshot.fromJson(result);
        final existing = _tables[id];
        if (existing == null) {
          _tables[id] = UiTableController(
            workspace: destination.id,
            snapshot: snapshot,
            read: widget.onReadTable,
            update: widget.onUpdateTableView,
          );
          _tables[id]!.addListener(() {
            final snapshot = _tables[id]!.snapshot;
            for (final project in store.projects) {
              for (final artifact in project.artifacts.where(
                (a) => a.id == id,
              )) {
                artifact.data = {
                  ...snapshot.toJson(),
                  '_revision': snapshot.revision,
                };
                artifact.editorState = {
                  ...artifact.editorState,
                  'filters': snapshot.filters.map((f) => f.toJson()).toList(),
                  'sort': snapshot.sort?.toJson(),
                  'visibleColumns': snapshot.visibleColumns,
                  'filteredRows': snapshot.filteredRows,
                };
              }
            }
            store.save();
          });
        } else if (snapshot.rows.isEmpty &&
            snapshot.revision > existing.snapshot.revision &&
            existing.read != null) {
          unawaited(existing.acceptSavedView(snapshot));
        } else {
          existing.accept(snapshot);
        }
        result = _tables[id]!.snapshot.toJson();
        _tableErrors.remove(id);
      } catch (_) {
        return;
      }
    }
    final content = result['content'] is Map
        ? Map<String, dynamic>.from(result['content'] as Map)
        : result;
    final artifact = WorkspaceArtifact(
      id: id,
      title: result['title'] as String? ?? 'Untitled work',
      kind: result['kind'] as String? ?? 'document',
      content: content['source'] as String? ?? '',
      data: {
        ...content,
        if (result['revision'] != null) '_revision': result['revision'],
      },
      editorState: content['editorState'] is Map
          ? Map<String, dynamic>.from(content['editorState'] as Map)
          : result['kind'] == 'table'
          ? {
              'filters': result['filters'],
              'sort': result['sort'],
              'filteredRows': result['filteredRows'],
              'visibleColumns': result['visibleColumns'],
            }
          : null,
    );
    final existing = destination.artifacts.where((a) => a.id == id).firstOrNull;
    if (existing == null) {
      destination.artifacts.add(artifact);
      store.save();
    } else if ((existing.data['_revision'] as num? ?? 0) <=
        (artifact.data['_revision'] as num? ?? 0)) {
      existing.title = artifact.title;
      existing.kind = artifact.kind;
      existing.content = artifact.content;
      existing.data = artifact.data;
      if (artifact.editorState.isNotEmpty) {
        existing.editorState = {
          ...existing.editorState,
          ...artifact.editorState,
        };
      }
      store.save();
    }
    if (reveal && destination.id == store.currentProject.id) {
      store.openArtifact(id);
    }
  }

  Future<void> _open(WorkspaceArtifact a) async {
    if (_home) setState(() => _homeChoice = false);
    final destination = store.currentProject;
    if (a.kind == 'app') {
      if (a.data['app'] == 'csharp' && !store.developerMode) return;
      await _launchApp(a.data['app'] as String);
      return;
    }
    if (widget.programmingClient != null && !a.remoteManaged) {
      _navigator.currentState?.push(
        MaterialPageRoute<void>(
          builder: (_) => Scaffold(
            appBar: AppBar(title: Text(a.title)),
            body: _editor(a),
          ),
        ),
      );
      return;
    }
    store.openArtifact(a.id);
    if (a.kind == 'table' && !_tables.containsKey(a.id)) {
      if (widget.onReadTable != null) {
        try {
          final snapshot = await widget.onReadTable!(destination.id, a.id);
          if (mounted) {
            _accept(snapshot.toJson(), project: destination, reveal: false);
          }
        } catch (e) {
          _messenger.currentState?.showSnackBar(
            SnackBar(content: Text('Could not open table: $e')),
          );
        }
      } else {
        try {
          _accept(a.data);
        } catch (_) {}
      }
    }
  }

  IconData _icon(String kind) => switch (kind) {
    'table' => Icons.table_chart_outlined,
    'chart' => Icons.bar_chart_rounded,
    'image' => Icons.image_outlined,
    'brain' => Icons.hub_outlined,
    'diagram' => Icons.draw_outlined,
    _ => Icons.description_outlined,
  };
  Future<void> _hydrateTable(
    WorkspaceArtifact artifact,
    WorkspaceProject project,
  ) async {
    try {
      final snapshot = widget.onReadTable == null
          ? TableSnapshot.fromJson(artifact.data)
          : await widget.onReadTable!(project.id, artifact.id);
      if (mounted) _accept(snapshot.toJson(), project: project, reveal: false);
    } catch (e) {
      _tableErrors[artifact.id] = 'Could not load table: $e';
    } finally {
      _hydratingTables.remove(artifact.id);
      if (mounted) setState(() {});
    }
  }

  Widget _editor(WorkspaceArtifact a) {
    if ((a.kind == 'surface' || a.data['surface'] is Map) &&
        widget.programmingClient != null) {
      final client = widget.programmingClient!;
      final surface = Map<String, dynamic>.from(
        a.data['surface'] as Map? ?? a.data,
      );
      final workspace = store.currentProject.id;
      return _region(
        a.title,
        NeuronView(
          kind: surface['kind'] as String? ?? 'surface',
          name: surface['name'] as String,
          scenes: _scenes.putIfAbsent(
            workspace,
            () => client.scenes(workspace),
          ),
          load: (kind, name) => client.appRequest(
            workspace,
            'node?${Uri(queryParameters: {'kind': kind, 'name': name}).query}',
          ),
          watch: client.grpcLive
              ? (kind, name) =>
                    client.watchNode(workspace, kind, name).map((_) {})
              : null,
          onActivate: (result) => _accept(result),
          onAction: (event) async {
            await client.appRequest(workspace, 'event', body: event);
          },
        ),
      );
    }
    if (a.kind == 'app') {
      final client = widget.programmingClient;
      if (client == null) {
        return const Center(
          child: Text('Connect to IntoChat to open local apps.'),
        );
      }
      if (a.data['app'] == 'csharp') {
        if (!store.developerMode) {
          return const Center(
            child: Text('C# files are available in developer mode only.'),
          );
        }
        final project = store.currentProject;
        return CSharpManager(
          key: ValueKey('${project.id}-${a.id}'),
          initialId: a.data['selected'] as String?,
          active: !project.presentation.minimizedArtifactIds.contains(a.id),
          request: (method, path, {body}) =>
              client.csharpRequest(project.id, method, path, body: body),
          onSelected: (id) {
            a.data['selected'] = id;
            store.save();
          },
          onAsk: (id, prompt) => _openApplication(
            'assistant',
            arguments: {'draft': prompt, 'title': 'C# app: $id'},
          ),
        );
      }
      return const Center(child: Text('This application has been retired.'));
    }

    if (a.kind == 'table' && _tables[a.id] == null) {
      if (!_hydratingTables.contains(a.id) && !_tableErrors.containsKey(a.id)) {
        _hydratingTables.add(a.id);
        final project = store.currentProject;
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) _hydrateTable(a, project);
        });
      }
      return _region(
        a.title,
        Center(
          child: _tableErrors[a.id] == null
              ? const CircularProgressIndicator()
              : Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(_tableErrors[a.id]!),
                    TextButton(
                      onPressed: () {
                        _tableErrors.remove(a.id);
                        setState(() {});
                      },
                      child: const Text('Retry'),
                    ),
                  ],
                ),
        ),
      );
    }
    if (a.kind == 'table') {
      return _region(a.title, UiDataTable(controller: _tables[a.id]!));
    }
    return _region(
      a.title,
      Center(child: Text('This window type is not available yet: ${a.kind}')),
    );
  }

  Widget _region(String title, Widget child) => Semantics(
    container: true,
    explicitChildNodes: true,
    role: SemanticsRole.region,
    label: title,
    child: child,
  );

  Widget _pane(WorkspaceArtifact a) => _editor(a);
  Future<void> _projectFiles(BuildContext context) async {
    final project = store.currentProject;
    final choice = await showDialog<(String, String)>(
      context: context,
      builder: (dialogContext) {
        var query = '';
        return StatefulBuilder(
          builder: (context, update) => AlertDialog(
            title: const Text('Saved work'),
            content: SizedBox(
              width: 560,
              height: 400,
              child: Column(
                children: [
                  TextField(
                    autofocus: true,
                    decoration: const InputDecoration(
                      hintText: 'Find a table, document or image',
                      prefixIcon: Icon(Icons.search),
                    ),
                    onChanged: (value) =>
                        update(() => query = value.toLowerCase()),
                  ),
                  const SizedBox(height: 16),
                  Expanded(
                    child: ListView(
                      children: [
                        if (project.artifacts.isEmpty)
                          const Padding(
                            padding: EdgeInsets.all(24),
                            child: Text('Your saved work will appear here.'),
                          ),
                        for (final a in project.artifacts.where(
                          (a) => a.title.toLowerCase().contains(query),
                        ))
                          ListTile(
                            contentPadding: EdgeInsets.zero,
                            leading: Icon(_icon(a.kind), size: 22),
                            title: Text(
                              a.title,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                            ),
                            subtitle: Text(a.kind),
                            onTap: () =>
                                Navigator.pop(dialogContext, (a.id, 'open')),
                            trailing: PopupMenuButton<String>(
                              tooltip: 'Open options',
                              onSelected: (mode) =>
                                  Navigator.pop(dialogContext, (a.id, mode)),
                              itemBuilder: (_) => const [
                                PopupMenuItem(
                                  value: 'beside',
                                  child: Text('Open beside'),
                                ),
                                PopupMenuItem(
                                  value: 'floating',
                                  child: Text('Open as window'),
                                ),
                              ],
                            ),
                          ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.pop(dialogContext),
                child: const Text('Done'),
              ),
            ],
          ),
        );
      },
    );
    if (!mounted || choice == null || store.currentProject.id != project.id) {
      return;
    }
    if (choice.$2 == 'beside') {
      store.openBeside(choice.$1);
    } else if (choice.$2 == 'floating') {
      store.openArtifact(choice.$1, placement: 'floating');
    }
    await _open(project.artifacts.firstWhere((a) => a.id == choice.$1));
  }

  Future<void> _remoteFocus(String workspace, String? window) async {
    final client = widget.programmingClient;
    if (client == null) return;
    try {
      final snapshot = await client.focusWorkspaceWindow(
        workspace,
        window,
        store.remoteRevisions[workspace] ?? 0,
      );
      if (!mounted) return;
      _animateFocus = true;
      store.reconcileWorkspace(
        store.projects.firstWhere((p) => p.id == workspace),
        snapshot,
      );
    } catch (_) {
      await _remote[workspace]?.refresh();
    }
  }

  Future<void> _configurePresentation(
    WorkspaceProject project,
    WorkspaceSnapshot snapshot,
  ) async {
    if (snapshot.home != null ||
        snapshot.companion != null ||
        !_presentationConfigured.add(project.id)) {
      return;
    }
    final client = widget.programmingClient!;
    try {
      final manifests = await Future.wait([
        _manifest(client, project.id, 'ino'),
        _manifest(client, project.id, 'assistant'),
      ]);
      if (!mounted) return;
      Map<String, dynamic>? entry(String app, AppManifest manifest) =>
          manifest.entry == null
          ? null
          : {
              'kind': manifest.entry!.kind,
              'name': '${project.id}/apps/$app/${manifest.entry!.name}',
            };
      final home = entry('ino', manifests[0]);
      final companion = entry('assistant', manifests[1]);
      if (home == null && companion == null) return;
      var latest = snapshot;
      for (var attempt = 0; attempt < 2; attempt++) {
        try {
          final configured = await client.configureWorkspacePresentation(
            project.id,
            home: latest.home ?? home,
            companion: latest.companion ?? companion,
            expectedRevision: latest.revision,
          );
          if (mounted) store.reconcileWorkspace(project, configured);
          return;
        } on TableRequestException catch (error) {
          if (error.statusCode != 409 || attempt == 1) rethrow;
          latest = await client.readWorkspace(project.id);
          if (latest.home != null && latest.companion != null) {
            if (mounted) store.reconcileWorkspace(project, latest);
            return;
          }
        }
      }
    } catch (_) {
      _presentationConfigured.remove(project.id);
    }
  }

  Widget _rootView(Map<String, dynamic> root, String label) {
    final client = widget.programmingClient!;
    final workspace = store.currentProject.id;
    return _region(
      label,
      NeuronView(
        key: ValueKey('$workspace/${root['kind']}/${root['name']}'),
        kind: root['kind'] as String,
        name: root['name'] as String,
        scenes: _scenes.putIfAbsent(workspace, () => client.scenes(workspace)),
        watchBrain: _brainWatches.putIfAbsent(
          workspace,
          () =>
              () => client.watchBrain(brainId: workspace),
        ),
        load: (kind, name) => client.appRequest(
          workspace,
          'node?${Uri(queryParameters: {'kind': kind, 'name': name}).query}',
        ),
        watch: client.grpcLive
            ? (kind, name) =>
                  client.watchNode(workspace, kind, name).map((_) {})
            : null,
        onActivate: _accept,
        onAction: (event) async {
          await client.appRequest(workspace, 'event', body: event);
        },
      ),
    );
  }

  void _showCatalog(BuildContext context) => showDialog<void>(
    context: context,
    builder: (_) => Dialog.fullscreen(
      child: Scaffold(
        appBar: AppBar(
          title: const Text('Apps and brain'),
          leading: IconButton(
            tooltip: 'Close apps',
            icon: const Icon(Icons.close),
            onPressed: () => Navigator.of(context).pop(),
          ),
        ),
        body: _homeScreen(context),
      ),
    ),
  );

  Widget _main(BuildContext context) {
    if (widget.programmingClient == null) {
      return _home ? _homeScreen(context) : _work(context);
    }
    final project = store.currentProject;
    final snapshot = store.remoteSnapshots[project.id];
    final home = snapshot?.home;
    final companion = snapshot?.companion;
    final tokens = UiTokens.of(context);
    final main = Column(
      children: [
        Row(
          children: [
            TextButton.icon(
              onPressed: () => _showCatalog(context),
              icon: const Icon(Icons.apps_rounded),
              label: const Text('Apps and brain'),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: SingleChildScrollView(
                scrollDirection: Axis.horizontal,
                child: Row(
                  children: [
                    for (final id in project.presentation.openArtifactIds)
                      Padding(
                        padding: EdgeInsets.only(right: tokens.spaceSmall),
                        child: InputChip(
                          key: ValueKey('workspace-entry-$id'),
                          label: Text(
                            project.artifacts
                                    .where((a) => a.id == id)
                                    .firstOrNull
                                    ?.title ??
                                'Untitled',
                          ),
                          selected: store.focusedWindow == id,
                          onPressed: () => store.focusWindow(id),
                          onDeleted: () => store.closeArtifact(id),
                        ),
                      ),
                  ],
                ),
              ),
            ),
          ],
        ),
        Expanded(
          child: UiMotion(
            contentKey: '${project.id}/${store.focusedWindow ?? 'home'}',
            animate: _animateFocus,
            child: _home
                ? (home == null
                      ? _homeScreen(context)
                      : _rootView(home, 'Home'))
                : _work(context),
          ),
        ),
      ],
    );
    final focusedRoot = project.artifacts
        .where((a) => a.id == store.focusedWindow)
        .firstOrNull
        ?.data['surface'];
    if (companion == null ||
        (focusedRoot is Map &&
            focusedRoot['kind'] == companion['kind'] &&
            focusedRoot['name'] == companion['name'])) {
      return main;
    }
    Widget assistant(bool wide) => UiSurface(
      padding: EdgeInsets.all(tokens.spaceSmall),
      child: Column(
        children: [
          Row(
            children: [
              Icon(
                Icons.auto_awesome_outlined,
                size: 18,
                color: Theme.of(context).colorScheme.primary,
              ),
              const SizedBox(width: 8),
              Text('Assistant', style: Theme.of(context).textTheme.titleSmall),
              const Spacer(),
              IconButton(
                tooltip: _companionExpanded
                    ? 'Collapse Assistant'
                    : 'Expand Assistant',
                onPressed: () =>
                    setState(() => _companionExpanded = !_companionExpanded),
                icon: Icon(
                  _companionExpanded ? Icons.expand_more : Icons.expand_less,
                ),
              ),
            ],
          ),
          if (_companionExpanded)
            Expanded(child: _rootView(companion, 'Assistant')),
        ],
      ),
    );
    return LayoutBuilder(
      builder: (context, constraints) {
        if (constraints.maxWidth >= 1000) {
          return Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Expanded(child: main),
              SizedBox(width: tokens.spaceMedium),
              SizedBox(
                width: _companionExpanded ? 360 : 180,
                child: assistant(true),
              ),
            ],
          );
        }
        return Column(
          children: [
            Expanded(child: main),
            SizedBox(height: tokens.spaceSmall),
            SizedBox(
              height: _companionExpanded ? constraints.maxHeight * .36 : 64,
              child: assistant(false),
            ),
          ],
        );
      },
    );
  }

  Widget _homeScreen(BuildContext context) {
    final client = widget.programmingClient;
    final project = store.currentProject;
    return HomeScreen(
      key: ValueKey('${client?.workspaceIdentity}/${project.id}'),
      workspaceId: project.id,
      request: client?.jsonRequest,
      loadIcon: client?.readBytes,
      localApps: [
        if (store.developerMode)
          const LocalApp(id: 'csharp', title: 'C# files', icon: Icons.code),
      ],
      onLaunchLocal: _launchApp,
      onOpen: _openApplication,
      watchBrain: client == null
          ? null
          : () => client.watchBrain(brainId: project.id),
      loadSignals: client == null
          ? null
          : (since, limit) =>
                client.signals(project.id, since: since, limit: limit),
      onFocus: (id) {
        store.focusWindow(id);
        setState(() => _homeChoice = false);
      },
      onStop: store.closeArtifact,
      infrastructure: client == null
          ? null
          : InfrastructureStrip(
              watch: () => client.watchDeployment(project.id),
              onTap: (_) => _openConnections(context),
            ),
      decorateIcon: client == null
          ? null
          : (app, icon) => app.id == 'circle'
                ? CircleActivityBadge(
                    feed: _circleFeeds.putIfAbsent(
                      project.id,
                      () => CircleActivityFeed(
                        () => client.watchCircleActivity(),
                      ),
                    ),
                    homeShown: _home,
                    child: icon,
                  )
                : icon,
      onAppsChanged: () {
        _manifests.clear();
        _apps.remove(project.id);
        _appsLoading.remove(project.id);
        _loadApps(project.id);
      },
    );
  }

  // The working area: the focused window, or the assistant's docked surface when nothing is open.
  Widget _work(BuildContext context) {
    final client = widget.programmingClient;
    final project = store.currentProject;
    if (store.focusedWindow == null) {
      if (client == null) {
        return const Center(child: Text('Open something from Home.'));
      }
      return const Center(child: Text('Open something from Home.'));
    }
    return WorkspaceDesktop(
      key: ValueKey('desktop-${project.id}'),
      store: store,
      editorBuilder: _pane,
      editorStateToken: (artifact) => Object.hash(
        _tables[artifact.id],
        _tableErrors[artifact.id],
        artifact.data['selected'],
        artifact.data['refresh'],
      ),
    );
  }

  final _manifests = <String, Future<AppManifest>>{};

  Future<AppManifest> _manifest(
    DigitalBrainUiClient client,
    String brain,
    String app,
  ) => _manifests['$brain/$app'] ??= client.readAppManifest(brain, app);

  // The dock shows the focused app's toolbar. Without a focused window, or when the focused
  // app declares no toolbar, it shows the assistant's: the assistant is the default focused app.
  Future<(String, AppManifest)> _dockOf(
    DigitalBrainUiClient client,
    String brain,
    String? focused,
  ) async {
    final app = focused == null ? null : appOfWindow(focused);
    if (app != null) {
      final manifest = await _manifest(client, brain, app);
      if (manifest.toolbar.isNotEmpty) return (app, manifest);
    }
    return ('assistant', await _manifest(client, brain, 'assistant'));
  }

  Widget _dock(BuildContext context) {
    final client = widget.programmingClient;
    if (client == null) return const SizedBox(width: 40, height: 40);
    final project = store.currentProject;
    final focused = store.focusedWindow;
    return FutureBuilder<(String, AppManifest)>(
      key: ValueKey('dock-${project.id}-${focused ?? 'assistant'}'),
      future: _dockOf(client, project.id, focused),
      builder: (context, snapshot) {
        final shown = snapshot.data;
        if (shown == null) return const SizedBox(width: 40, height: 40);
        final (app, manifest) = shown;
        if (app == 'assistant' &&
            (_companionExpanded || appOfWindow(focused ?? '') == 'assistant') &&
            store.remoteSnapshots[project.id]?.companion != null) {
          return const SizedBox.shrink();
        }
        return Dock(
          app: app,
          manifest: manifest,
          loadIcon: client.readBytes,
          iconRoute:
              '/brains/${Uri.encodeComponent(project.id)}/apps/${Uri.encodeComponent(app)}/icon',
          watchActivity: () => client.watchActivity(project.id),
          brainId: project.id,
          floating: focused == null,
          onRequest: (operation, input) =>
              client.requestApp(project.id, app, operation, input),
        );
      },
    );
  }

  Widget _workspace(BuildContext context) => _main(context);
  @override
  Widget build(BuildContext context) => MaterialApp(
    navigatorKey: _navigator,
    navigatorObservers: [_routes],
    initialRoute: '/',
    scaffoldMessengerKey: _messenger,
    title: 'IntoChat',
    debugShowCheckedModeBanner: false,
    theme: UiTheme.workspace(Brightness.light).copyWith(
      visualDensity: store.settings.compactDensity
          ? VisualDensity.compact
          : VisualDensity.standard,
    ),
    darkTheme: UiTheme.workspaceDark(store.settings.theme).copyWith(
      visualDensity: store.settings.compactDensity
          ? VisualDensity.compact
          : VisualDensity.standard,
    ),
    themeAnimationDuration: store.settings.reducedMotion
        ? Duration.zero
        : kThemeAnimationDuration,
    builder: (context, child) {
      if (_ready) {
        _routes.sync(
          _directory
              ? '/projects'
              : '/projects/${store.currentProject.id}/workspace',
        );
      }
      return MediaQuery(
        data: MediaQuery.of(context).copyWith(
          disableAnimations:
              store.settings.reducedMotion ||
              MediaQuery.of(context).disableAnimations,
        ),
        child: UiThemeScope(
          brightness: Theme.of(context).brightness,
          child: Theme(data: Theme.of(context), child: child!),
        ),
      );
    },
    themeMode: UiTheme.modeFor(store.settings.theme),
    home: Builder(
      builder: (context) => Scaffold(
        body: !_ready
            ? Center(
                child: store.loadFailed
                    ? Column(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text(store.persistenceError!),
                          TextButton(
                            onPressed: () async {
                              await store.reload();
                              if (mounted) await _load();
                            },
                            child: const Text('Retry loading'),
                          ),
                        ],
                      )
                    : const CircularProgressIndicator(),
              )
            : Column(
                children: [
                  if (widget.statusMessage != null)
                    MaterialBanner(
                      content: Text(widget.statusMessage!),
                      actions: const [SizedBox.shrink()],
                    ),
                  if (store.persistenceError != null)
                    MaterialBanner(
                      content: Text(store.persistenceError!),
                      actions: [
                        TextButton(
                          onPressed: store.save,
                          child: const Text('Retry saving'),
                        ),
                        TextButton(
                          onPressed: () => _reloadSavedState(context),
                          child: const Text('Reload saved state'),
                        ),
                      ],
                    ),
                  if (widget.programmingClient == null && widget.store == null)
                    const Text(
                      'Offline session: changes are temporary and will be lost when this session closes.',
                    ),
                  Expanded(
                    child: Padding(
                      padding: const EdgeInsets.fromLTRB(12, 10, 12, 0),
                      child: KeyedSubtree(
                        key: ValueKey(_reloadGeneration),
                        child: _workspace(context),
                      ),
                    ),
                  ),
                  _islands(context),
                ],
              ),
      ),
    ),
  );
  Widget _islands(BuildContext context) => WorkspaceIslands(
    store: store,
    dock: _dock(context),
    onHome: () {
      if (widget.programmingClient != null) {
        unawaited(_remoteFocus(store.currentProject.id, null));
      } else {
        setState(() => _homeChoice = !_home);
      }
    },
    onConnections: widget.programmingClient == null
        ? null
        : () => _openConnections(context),
    home: _home,
    onNewWorkspace: () async {
      final input = TextEditingController();
      final name = await showDialog<String>(
        context: context,
        builder: (context) => AlertDialog(
          title: const Text('New workspace'),
          content: TextField(
            controller: input,
            autofocus: true,
            decoration: const InputDecoration(labelText: 'Workspace name'),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(context, input.text),
              child: const Text('Create workspace'),
            ),
          ],
        ),
      );
      if (name != null && mounted) {
        try {
          final client = widget.programmingClient;
          final id = client == null ? null : await client.createWorkspace();
          if (!mounted) return;
          store.createProject(
            name.trim().isEmpty ? 'Untitled workspace' : name,
            id: id,
          );
        } on Object {
          if (mounted) {
            _messenger.currentState?.showSnackBar(
              const SnackBar(
                content: Text('Could not create workspace. Please try again.'),
              ),
            );
          }
        }
      }
    },
    onSwitchAccount: widget.onSwitchAccount,
    onSavedWork: () => _projectFiles(context),
    onSearch: () => _search(context),
    onCompute: () => _openCompute(context),
    onSettings: widget.programmingClient == null
        ? () => _openConfiguration(context)
        : () => _openApplication('settings'),
    onConfiguration: widget.programmingClient == null
        ? null
        : () => _openConfiguration(context),
  );

  void _openConfiguration(BuildContext context) => Navigator.of(context).push(
    MaterialPageRoute<void>(
      settings: const RouteSettings(name: '/settings/general'),
      builder: (_) => _settingsApp(() => Navigator.of(context).pop()),
    ),
  );

  void _openConnections(BuildContext context, [String? connectorId]) {
    final request = _connectionsRequest;
    if (request == null) return;
    showDialog<void>(
      context: context,
      builder: (dialogContext) => Dialog(
        clipBehavior: Clip.antiAlias,
        child: SizedBox(
          width: 560,
          height: 520,
          child: ConnectWindow(
            request: request,
            connectorRequest: _connectorRequest,
            initialConnectorId: connectorId,
            onOpen: widget.onOpenUrl,
          ),
        ),
      ),
    );
  }

  void _openCompute(BuildContext context) {
    final client = widget.programmingClient;
    final project = store.currentProject;
    Widget panel(BuildContext dialogContext) => ComputeUsagePanel(
      client: client,
      workspaceId: project.id,
      workspaceName: project.title,
      refreshSignal: _computeRefresh,
      onClose: () => Navigator.of(dialogContext).pop(),
    );
    final size = MediaQuery.sizeOf(context);
    if (size.width < 600) {
      showModalBottomSheet<void>(
        context: context,
        useSafeArea: true,
        isScrollControlled: true,
        showDragHandle: true,
        builder: (sheetContext) => SizedBox(
          height: MediaQuery.sizeOf(sheetContext).height * .85,
          child: panel(sheetContext),
        ),
      );
      return;
    }
    showDialog<void>(
      context: context,
      builder: (dialogContext) => Dialog(
        alignment: Alignment.bottomRight,
        insetPadding: const EdgeInsets.only(
          left: 12,
          right: 12,
          top: 12,
          bottom: 76,
        ),
        clipBehavior: Clip.antiAlias,
        child: SizedBox(
          width: 480,
          height: (size.height - 110).clamp(280.0, 680.0),
          child: panel(dialogContext),
        ),
      ),
    );
  }

  Widget _settingsApp(VoidCallback onClose, {String section = 'general'}) =>
      WorkspaceSettings(
        store: store,
        initialSection: section,
        onClose: onClose,
        kernelBaseUri: widget.kernelBaseUri,
        onOpen: widget.onOpenUrl,
        connectionsRequest: _connectionsRequest,
        connectorRequest: _connectorRequest,
        loadModels: widget.programmingClient?.readModelCatalog,
      );

  ConnectionsRequest? get _connectorRequest {
    final client = widget.programmingClient;
    if (client == null) return null;
    final workspaceId = store.currentProject.id;
    return (path, {body}) =>
        client.connectorRequest(workspaceId, path, body: body);
  }

  ConnectionsRequest? get _connectionsRequest {
    final client = widget.programmingClient;
    if (client == null) return null;
    final workspaceId = store.currentProject.id;
    return (path, {body}) =>
        client.workspaceConnectionsRequest(workspaceId, path, body: body);
  }

  Future<void> _openApplication(
    String application, {
    Map<String, dynamic> arguments = const {},
  }) async {
    final client = widget.programmingClient;
    if (client == null) return;
    final project = store.currentProject;
    try {
      final result = await client.openApp(project.id, application, arguments);
      if (!mounted) return;
      _animateFocus = true;
      store.reconcileWorkspace(project, result);
      _manifests.remove('${project.id}/$application');
      _navigator.currentState?.popUntil((route) => route.isFirst);
    } catch (error) {
      if (mounted) {
        _messenger.currentState?.showSnackBar(
          SnackBar(content: Text('Could not open application: $error')),
        );
      }
    }
  }

  Future<void> _launchApp(String appId) async {
    switch (appId) {
      case 'csharp':
        if (store.developerMode) {
          if (widget.programmingClient != null) {
            final artifact = WorkspaceArtifact(
              id: 'local-csharp',
              title: 'C# files',
              kind: 'app',
              data: {'app': 'csharp'},
            );
            _navigator.currentState?.push(
              MaterialPageRoute<void>(
                builder: (_) => Scaffold(
                  appBar: AppBar(title: const Text('C# files')),
                  body: _editor(artifact),
                ),
              ),
            );
          } else {
            store.launchLocalApp(appId);
            setState(() => _homeChoice = false);
          }
        }
      default:
        await _openApplication(appId);
    }
  }

  void _search(BuildContext context) {
    _query = '';
    showDialog<void>(
      context: context,
      builder: (c) => StatefulBuilder(
        builder: (c, update) => AlertDialog(
          title: TextField(
            autofocus: true,
            decoration: const InputDecoration(hintText: 'Search project work'),
            onChanged: (v) => update(() => _query = v.toLowerCase()),
          ),
          content: SizedBox(
            width: 480,
            child: ListView(
              shrinkWrap: true,
              children: [
                for (final a in store.currentProject.artifacts.where(
                  (a) => a.title.toLowerCase().contains(_query),
                ))
                  ListTile(
                    leading: Icon(_icon(a.kind)),
                    title: Text(a.title),
                    onTap: () {
                      Navigator.pop(c);
                      _open(a);
                    },
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
