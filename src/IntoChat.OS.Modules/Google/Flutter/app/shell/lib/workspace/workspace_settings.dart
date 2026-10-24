import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';
import 'package:flutter/material.dart';

import '../connections/connect_window.dart';
import '../connections/connectors_menu.dart';
import 'model_settings.dart';
import 'settings/archived_tab.dart';
import 'settings/connections_tab.dart';
import 'settings/danger_tab.dart';
import 'settings/general_tab.dart';
import 'settings/keys_tab.dart';
import 'settings/models_tab.dart';
import 'settings/notifications_tab.dart';
import 'settings/settings_api.dart';
import 'settings/settings_tab_heading.dart';
import 'settings/variables_tab.dart';
import 'workspace_store.dart';

/// The Settings tabs, in order: (id, label). Billing is a disabled "Later"
/// entry after them.
const settingsTabs = <(String, String)>[
  ('general', 'General'),
  ('models', 'AI models'),
  ('keys', 'API keys'),
  ('connections', 'Connections'),
  ('variables', 'Variables and secrets'),
  ('notifications', 'Notifications'),
  ('archived', 'Archived chats'),
  ('danger', 'Danger zone'),
];

/// The tab id for [section], accepting the names older links used.
String settingsTabId(String section) {
  final id = switch (section) {
    'profile' || 'appearance' => 'general',
    'agents' => 'models',
    'developer' || 'gallery' || 'ui-kit' || 'advanced' => 'general',
    _ => section,
  };
  return settingsTabs.any((t) => t.$1 == id) ? id : 'general';
}

/// One brain's Settings: a tab host over the per-brain [api].
class WorkspaceSettings extends StatefulWidget {
  const WorkspaceSettings({
    super.key,
    required this.brainId,
    required this.store,
    this.initialTab = 'general',
    required this.onClose,
    this.api,
    this.kernelBaseUri,
    this.onOpen,
    this.connectionsRequest,
    this.connectorRequest,
    this.windowsRequest,
    this.loadModels,
  });
  final String brainId;
  final WorkspaceStore store;
  final String initialTab;
  final VoidCallback onClose;

  /// Entries, models and preferences for [brainId]; null when not connected.
  final SettingsApi? api;
  final Uri? kernelBaseUri;
  final OpenUrl? onOpen;
  final ConnectionsRequest? connectionsRequest,
      connectorRequest,
      windowsRequest;

  /// Used for the model list when there is no [api].
  final ModelCatalogLoader? loadModels;

  /// The section this screen was opened on, as given.
  String get initialSection => initialTab;

  @override
  State<WorkspaceSettings> createState() => _WorkspaceSettingsState();
}

class _WorkspaceSettingsState extends State<WorkspaceSettings> {
  late int _index = settingsTabs.indexWhere(
    (t) => t.$1 == settingsTabId(widget.initialTab),
  );
  ModelCatalogLoader? _loadModels;

  @override
  void initState() {
    super.initState();
    _refreshModels();
  }

  void _refreshModels() {
    final api = widget.api;
    _loadModels = api == null ? widget.loadModels : () => api.models();
  }

  // After a key changes, fetch the catalog now and hand that fetch to the
  // next model list; a new loader also makes a mounted ModelSettings reload.
  void _keysChanged() {
    final api = widget.api;
    if (api == null) return;
    Future<ChatModelCatalog>? pending = api.models()..ignore();
    setState(
      () => _loadModels = () {
        final fetched = pending;
        pending = null;
        return fetched ?? api.models();
      },
    );
  }

  void _select(String id) =>
      setState(() => _index = settingsTabs.indexWhere((t) => t.$1 == id));

  Widget _placeholder(String title, String description) =>
      SettingsTabHeading(title: title, description: description);

  Widget _notConnected(String title) => _placeholder(
    title,
    'Connect to your workspace to manage this brain\'s settings.',
  );

  Widget _content() {
    final api = widget.api;
    return switch (settingsTabs[_index].$1) {
      'models' => ModelsTab(
        store: widget.store,
        load: _loadModels,
        onAddKey: () => _select('keys'),
      ),
      'keys' =>
        api == null
            ? _notConnected('API keys')
            : KeysTab(api: api, onKeysChanged: _keysChanged),
      'connections' => ConnectionsTab(
        request: widget.connectionsRequest,
        connectorRequest: widget.connectorRequest,
        windowsRequest: widget.windowsRequest,
        onOpen: widget.onOpen,
      ),
      'variables' =>
        api == null
            ? _notConnected('Variables and secrets')
            : VariablesTab(api: api),
      'notifications' =>
        api == null
            ? _notConnected('Notifications')
            : NotificationsTab(api: api),
      'archived' =>
        api == null ? _notConnected('Archived chats') : ArchivedTab(api: api),
      'danger' => DangerTab(
        accountBase: widget.kernelBaseUri,
        onOpen: widget.onOpen,
      ),
      _ => GeneralTab(store: widget.store, api: api),
    };
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.store,
    builder: (context, _) => Scaffold(
      appBar: AppBar(
        leading: IconButton(
          tooltip: 'Back to workspace',
          onPressed: widget.onClose,
          icon: const Icon(Icons.arrow_back),
        ),
        title: const Text('Settings'),
      ),
      body: LayoutBuilder(
        builder: (context, constraints) {
          final pad = constraints.maxWidth < 700 ? 20.0 : 36.0;
          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Padding(
                padding: EdgeInsets.symmetric(horizontal: pad),
                child: UiKitTabs(
                  key: const Key('settings-tabs'),
                  labels: [
                    for (final t in settingsTabs) t.$2,
                    'Billing (Later)',
                  ],
                  index: _index,
                  disabled: {settingsTabs.length},
                  onChanged: (i) {
                    if (i < settingsTabs.length) {
                      _select(settingsTabs[i].$1);
                    }
                  },
                ),
              ),
              Expanded(
                child: SingleChildScrollView(
                  key: PageStorageKey(settingsTabs[_index].$1),
                  padding: EdgeInsets.all(pad),
                  child: Align(
                    alignment: Alignment.topLeft,
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(maxWidth: 780),
                      child: KeyedSubtree(
                        key: ValueKey(settingsTabs[_index].$1),
                        child: _content(),
                      ),
                    ),
                  ),
                ),
              ),
            ],
          );
        },
      ),
    ),
  );
}
