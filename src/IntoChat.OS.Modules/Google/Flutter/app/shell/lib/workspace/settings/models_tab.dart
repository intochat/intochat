import 'package:flutter/material.dart';

import '../model_settings.dart';
import '../workspace_store.dart';
import 'settings_tab_heading.dart';

/// AI models: the default-model select over the brain's catalog. Models that
/// need a key link to the API keys tab through [onAddKey].
class ModelsTab extends StatelessWidget {
  const ModelsTab({
    super.key,
    required this.store,
    required this.load,
    required this.onAddKey,
  });
  final WorkspaceStore store;
  final ModelCatalogLoader? load;
  final VoidCallback onAddKey;

  @override
  Widget build(BuildContext context) {
    final prefs = store.settings;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const SettingsTabHeading(
          title: 'AI models',
          description: 'Models this brain can use. Add a provider key in API keys to unlock more.',
        ),
        ModelSettings(
          load: load,
          selectedId: prefs.defaultModelProfile,
          onAddKey: onAddKey,
          select: true,
          onSelected: (id) {
            prefs.defaultModelProfile = id;
            store.save();
          },
        ),
        if (store.persistenceError != null) Text(store.persistenceError!),
      ],
    );
  }
}
