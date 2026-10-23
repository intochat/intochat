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
    final name = _name.text.trim();
    widget.store.settings.displayName = name.runes.length > 120
        ? String.fromCharCodes(name.runes.take(120))
        : name;
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

  Widget _toggle(
    String title,
    String detail,
    bool value,
    ValueChanged<bool> set,
  ) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: type.body.copyWith(color: c.strong)),
                Text(detail, style: type.bodyMuted),
              ],
            ),
          ),
          const SizedBox(width: 12),
          UiKitSwitch(
            value: value,
            semanticLabel: title,
            onChanged: (v) {
              set(v);
              widget.store.save();
            },
          ),
        ],
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
        UiKitField(
          label: 'Display name',
          controller: _name,
          fieldKey: const Key('settings-display-name'),
          helper:
              'A display preference; your signed-in account stays the same.',
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
        UiKitSelect<String>(
          key: const Key('theme_picker'),
          label: 'Theme',
          value: UiTheme.paletteName(prefs.theme),
          items: const [
            UiKitSelectItem('intochat', 'IntoChat'),
            UiKitSelectItem('midnight', 'Midnight'),
            UiKitSelectItem('paper', 'Paper'),
            UiKitSelectItem('lumen', 'Lumen'),
          ],
          onChanged: (v) {
            prefs.theme = v;
            store.save();
          },
        ),
        const SizedBox(height: 16),
        _toggle(
          'Compact spacing',
          'Use a denser layout for workspace controls.',
          prefs.compactDensity,
          (v) => prefs.compactDensity = v,
        ),
        _toggle(
          'Reduce motion',
          'Minimize transitions and animated movement.',
          prefs.reducedMotion,
          (v) => prefs.reducedMotion = v,
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
