import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import '../workspace_store.dart';
import 'settings_tab_heading.dart';

/// General: display name and appearance, with Advanced (storage and the UI
/// Kit) at the bottom.
class GeneralTab extends StatefulWidget {
  const GeneralTab({super.key, required this.store});
  final WorkspaceStore store;
  @override
  State<GeneralTab> createState() => _GeneralTabState();
}

class _GeneralTabState extends State<GeneralTab> {
  late final TextEditingController _name = TextEditingController(
    text: widget.store.settings.displayName,
  );
  bool _saving = false;
  String? _notice;

  @override
  void dispose() {
    _name.dispose();
    super.dispose();
  }

  Future<void> _saveName() async {
    setState(() {
      _saving = true;
      _notice = null;
    });
    widget.store.settings.displayName = _name.text.trim();
    await widget.store.save();
    if (mounted) {
      setState(() {
        _saving = false;
        _notice = widget.store.persistenceError ?? 'Name saved.';
      });
    }
  }

  void _openUiKit() => Navigator.of(context).push(
    MaterialPageRoute<void>(
      settings: const RouteSettings(name: '/settings/ui-kit'),
      builder: (_) => Scaffold(
        appBar: AppBar(title: const Text('UI Kit')),
        body: const UiGalleryScreen(),
      ),
    ),
  );

  Widget _section(String title) {
    final c = UiColors.of(context);
    return Padding(
      padding: const EdgeInsets.only(top: 32, bottom: 16),
      child: Text(
        title,
        style: UiType.of(context).cardTitle.copyWith(color: c.strong),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final store = widget.store;
    final prefs = store.settings;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const SettingsTabHeading(
          title: 'General',
          description: 'Personal preferences for all projects on this device.',
        ),
        TextField(
          controller: _name,
          maxLength: 120,
          decoration: const InputDecoration(
            labelText: 'Display name',
            helperText:
                'A display preference; your signed-in account stays the same.',
            border: OutlineInputBorder(),
          ),
          onSubmitted: (_) => _saveName(),
        ),
        const SizedBox(height: 12),
        UiKitButton(
          label: _saving ? 'Saving…' : 'Save name',
          onPressed: _saving ? null : _saveName,
        ),
        if (_notice != null)
          Padding(
            padding: const EdgeInsets.only(top: 12),
            child: Text(_notice!),
          ),
        _section('Appearance'),
        DropdownButtonFormField<String>(
          key: const Key('theme_picker'),
          initialValue: UiTheme.paletteName(prefs.theme),
          decoration: const InputDecoration(
            labelText: 'Theme',
            border: OutlineInputBorder(),
          ),
          items: const [
            DropdownMenuItem(value: 'intochat', child: Text('IntoChat')),
            DropdownMenuItem(value: 'midnight', child: Text('Midnight')),
            DropdownMenuItem(value: 'paper', child: Text('Paper')),
            DropdownMenuItem(value: 'lumen', child: Text('Lumen')),
          ],
          onChanged: (v) {
            if (v != null) {
              prefs.theme = v;
              store.save();
            }
          },
        ),
        const SizedBox(height: 16),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Compact spacing'),
          subtitle: const Text('Use a denser layout for workspace controls.'),
          value: prefs.compactDensity,
          onChanged: (v) {
            prefs.compactDensity = v;
            store.save();
          },
        ),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Reduce motion'),
          subtitle: const Text('Minimize transitions and animated movement.'),
          value: prefs.reducedMotion,
          onChanged: (v) {
            prefs.reducedMotion = v;
            store.save();
          },
        ),
        _section('Advanced'),
        const ListTile(
          contentPadding: EdgeInsets.zero,
          leading: Icon(Icons.storage_outlined),
          title: Text('Local workspace storage'),
          subtitle: Text(
            'Appearance and personal preferences are saved on this device. Workspace navigation and app content are held by your brain.',
          ),
        ),
        const SizedBox(height: 8),
        Text(
          store.persistenceError ?? 'Local changes are saved automatically.',
        ),
        if (store.persistenceError != null)
          TextButton.icon(
            onPressed: _saving
                ? null
                : () async {
                    setState(() => _saving = true);
                    await store.save();
                    if (mounted) setState(() => _saving = false);
                  },
            icon: const Icon(Icons.refresh),
            label: const Text('Retry saving'),
          ),
        const SizedBox(height: 16),
        const Text(
          'Looking for app building blocks? Open UI Kit to explore Flutter neurons and interactive examples.',
        ),
        TextButton(
          key: const Key('settings-open-ui-kit'),
          onPressed: _openUiKit,
          child: const Text('Explore UI Kit'),
        ),
      ],
    );
  }
}
