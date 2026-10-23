import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';
// ignore: implementation_imports
import 'package:intochat_os_ui/src/composition/value_nodes/view_badge.dart';

import 'settings_api.dart';
import 'settings_tab_heading.dart';

const _ownerOnly = 'Only the brain owner can change API keys.';

/// API keys: one row per key-based provider. Values are write-only; a row
/// shows only whether its key is set and, when the server sends one, the
/// key's last four characters. [onKeysChanged] runs after a save or removal.
class KeysTab extends StatefulWidget {
  const KeysTab({super.key, required this.api, this.onKeysChanged});
  final SettingsApi api;
  final VoidCallback? onKeysChanged;
  @override
  State<KeysTab> createState() => _KeysTabState();
}

class _KeysTabState extends State<KeysTab> {
  Map<String, EntrySummary> _keys = const {};
  bool _loading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final entries = await widget.api.listEntries();
      if (!mounted) return;
      setState(
        () => _keys = {
          for (final e in entries)
            if (e.kind == EntryKind.secret &&
                e.name.startsWith(providerEntryPrefix))
              e.name.substring(providerEntryPrefix.length): e,
        },
      );
    } on SettingsForbidden {
      if (mounted) setState(() => _error = _ownerOnly);
    } catch (_) {
      if (mounted) setState(() => _error = 'API keys could not be loaded.');
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  String _failure(Object error) =>
      error is SettingsForbidden ? _ownerOnly : 'The key could not be saved.';

  Future<void> _edit(String id, String name) async {
    final saved = await showDialog<bool>(
      context: context,
      builder: (_) => _KeyDialog(
        provider: name,
        replace: _keys.containsKey(id),
        save: (value) => widget.api.putEntry(
          '$providerEntryPrefix$id',
          EntryKind.secret,
          value,
        ),
        describe: _failure,
      ),
    );
    if (saved == true) await _changed();
  }

  Future<void> _remove(String id, String name) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialog) => _SettingsDialog(
        title: 'Remove the $name key?',
        body:
            'Models from $name stop working in this brain until you add a key again.',
        actions: [
          UiKitButton(
            key: const Key('settings-key-remove-confirm'),
            label: 'Remove key',
            style: UiKitButtonStyle.danger,
            fullWidth: true,
            onPressed: () => Navigator.pop(dialog, true),
          ),
          UiKitButton(
            key: const Key('settings-key-remove-cancel'),
            label: 'Cancel',
            style: UiKitButtonStyle.secondary,
            fullWidth: true,
            onPressed: () => Navigator.pop(dialog, false),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    try {
      await widget.api.removeEntry('$providerEntryPrefix$id');
      await _changed();
    } catch (error) {
      if (mounted) {
        setState(
          () => _error = error is SettingsForbidden
              ? _ownerOnly
              : 'The key could not be removed.',
        );
      }
    }
  }

  Future<void> _changed() async {
    widget.onKeysChanged?.call();
    await _load();
  }

  Widget _row(String id, String name) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    final entry = _keys[id];
    final badge = entry == null
        ? 'Not set'
        : entry.tail == null
        ? 'Set'
        : 'Set ••••${entry.tail}';
    return Container(
      key: Key('settings-key-row-$id'),
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.all(11),
      decoration: BoxDecoration(
        color: c.surface,
        border: Border.all(color: c.hairline),
        borderRadius: BorderRadius.circular(UiTokens.of(context).controlRadius),
      ),
      child: Wrap(
        spacing: 10,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.center,
        alignment: WrapAlignment.spaceBetween,
        children: [
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                name,
                style: type.body.copyWith(
                  color: c.strong,
                  fontWeight: FontWeight.w600,
                ),
              ),
              const SizedBox(width: 10),
              ViewBadge(text: badge, tone: entry == null ? 0 : 1),
            ],
          ),
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              UiKitButton(
                key: Key('settings-key-add-$id'),
                label: entry == null ? 'Add' : 'Replace',
                style: UiKitButtonStyle.secondary,
                compact: true,
                onPressed: _loading ? null : () => _edit(id, name),
              ),
              if (entry != null) ...[
                const SizedBox(width: 8),
                UiKitButton(
                  key: Key('settings-key-remove-$id'),
                  label: 'Remove',
                  style: UiKitButtonStyle.ghost,
                  compact: true,
                  onPressed: _loading ? null : () => _remove(id, name),
                ),
              ],
            ],
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      const SettingsTabHeading(
        title: 'API keys',
        description: 'Keys are stored sealed for this brain. Once saved, a key is never shown again.',
      ),
      if (_loading) const LinearProgressIndicator(),
      if (_error != null)
        Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: Semantics(liveRegion: true, child: Text(_error!)),
        ),
      for (final (id, name) in keyProviders) _row(id, name),
    ],
  );
}

/// Add or replace one key. The field is cleared as soon as the save
/// succeeds; the value lives only in this dialog's controller.
class _KeyDialog extends StatefulWidget {
  const _KeyDialog({
    required this.provider,
    required this.replace,
    required this.save,
    required this.describe,
  });
  final String provider;
  final bool replace;
  final Future<void> Function(String value) save;
  final String Function(Object error) describe;
  @override
  State<_KeyDialog> createState() => _KeyDialogState();
}

class _KeyDialogState extends State<_KeyDialog> {
  final _field = TextEditingController();
  bool _saving = false;
  String? _error;

  @override
  void dispose() {
    _field.clear();
    _field.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final value = _field.text.trim();
    if (value.isEmpty) {
      setState(() => _error = 'Paste a key first.');
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await widget.save(value);
      _field.clear();
      if (mounted) Navigator.pop(context, true);
    } catch (error) {
      if (mounted) {
        setState(() {
          _saving = false;
          _error = widget.describe(error);
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) => _SettingsDialog(
    title: '${widget.replace ? 'Replace' : 'Add'} ${widget.provider} key',
    body: 'The key is sealed for this brain and cannot be read back.',
    field: UiKitField(
      label: 'API key',
      controller: _field,
      secret: true,
      enabled: !_saving,
      error: _error,
      fieldKey: const Key('settings-key-field'),
      onSubmitted: (_) => _save(),
    ),
    actions: [
      UiKitButton(
        key: const Key('settings-key-save'),
        label: _saving ? 'Saving…' : 'Save key',
        fullWidth: true,
        onPressed: _saving ? null : _save,
      ),
      UiKitButton(
        label: 'Cancel',
        style: UiKitButtonStyle.secondary,
        fullWidth: true,
        onPressed: _saving ? null : () => Navigator.pop(context, false),
      ),
    ],
  );
}

/// A kit-styled dialog with full-width actions.
class _SettingsDialog extends StatelessWidget {
  const _SettingsDialog({
    required this.title,
    required this.body,
    required this.actions,
    this.field,
  });
  final String title, body;
  final Widget? field;
  final List<Widget> actions;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    return Dialog(
      backgroundColor: c.surface,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(UiTokens.of(context).modalRadius),
      ),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(title, style: type.heading.copyWith(color: c.strong)),
              const SizedBox(height: 8),
              Text(body, style: type.bodyMuted),
              if (field != null) ...[const SizedBox(height: 16), field!],
              const SizedBox(height: 20),
              for (final (i, action) in actions.indexed) ...[
                if (i > 0) const SizedBox(height: 8),
                action,
              ],
            ],
          ),
        ),
      ),
    );
  }
}
