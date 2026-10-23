import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'settings_api.dart';
import 'settings_dialog.dart';
import 'settings_tab_heading.dart';

const _ownerOnly = 'Only the brain owner can change variables and secrets.';
final _validName = RegExp(r'^[A-Za-z0-9_.\-]+$');

/// Variables and secrets: every entry except the `provider:` API keys.
/// A variable shows its value; a secret shows only `••••` and its tail.
class VariablesTab extends StatefulWidget {
  const VariablesTab({super.key, required this.api});
  final SettingsApi api;
  @override
  State<VariablesTab> createState() => _VariablesTabState();
}

class _VariablesTabState extends State<VariablesTab> {
  List<EntrySummary> _entries = const [];
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
        () => _entries = [
          for (final e in entries)
            if (!e.name.startsWith(providerEntryPrefix)) e,
        ],
      );
    } on SettingsForbidden {
      if (mounted) setState(() => _error = _ownerOnly);
    } catch (_) {
      if (mounted) setState(() => _error = 'Entries could not be loaded.');
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  String _failure(Object error) =>
      error is SettingsForbidden ? _ownerOnly : 'The entry could not be saved.';

  Future<void> _add() async {
    final saved = await showDialog<bool>(
      context: context,
      builder: (_) =>
          _EntryDialog(save: widget.api.putEntry, describe: _failure),
    );
    if (saved == true) await _load();
  }

  Future<void> _remove(EntrySummary entry) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialog) => SettingsDialog(
        title: 'Remove ${entry.name}?',
        body: 'Apps and agents that use it will no longer find it.',
        actions: [
          UiKitButton(
            key: const Key('settings-variable-remove-confirm'),
            label: 'Remove',
            style: UiKitButtonStyle.danger,
            fullWidth: true,
            onPressed: () => Navigator.pop(dialog, true),
          ),
          UiKitButton(
            key: const Key('settings-variable-remove-cancel'),
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
      await widget.api.removeEntry(entry.name);
      await _load();
    } catch (error) {
      if (mounted) {
        setState(
          () => _error = error is SettingsForbidden
              ? _ownerOnly
              : 'The entry could not be removed.',
        );
      }
    }
  }

  Widget _row(EntrySummary entry) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    final secret = entry.kind == EntryKind.secret;
    final shown = secret ? '••••${entry.tail ?? ''}' : (entry.value ?? '');
    return Container(
      key: Key('settings-variable-row-${entry.name}'),
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.all(11),
      decoration: BoxDecoration(
        color: c.surface,
        border: Border.all(color: c.hairline),
        borderRadius: BorderRadius.circular(UiTokens.of(context).controlRadius),
      ),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  entry.name,
                  style: type.body.copyWith(
                    color: c.strong,
                    fontWeight: FontWeight.w600,
                  ),
                ),
                const SizedBox(height: 2),
                Text(shown, style: type.meta, overflow: TextOverflow.ellipsis),
              ],
            ),
          ),
          ViewBadge(text: secret ? 'Secret' : 'Variable', tone: secret ? 2 : 0),
          const SizedBox(width: 8),
          UiKitButton(
            key: Key('settings-variable-remove-${entry.name}'),
            label: 'Remove',
            style: UiKitButtonStyle.ghost,
            compact: true,
            onPressed: _loading ? null : () => _remove(entry),
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
        title: 'Variables and secrets',
        description: 'Values your brain\'s apps and agents can use. A secret is never shown again once saved.',
      ),
      if (_loading) const LinearProgressIndicator(),
      if (_error != null)
        Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: Semantics(liveRegion: true, child: Text(_error!)),
        ),
      if (!_loading && _error == null && _entries.isEmpty)
        Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: Text(
            'No variables or secrets yet.',
            style: UiType.of(context).bodyMuted,
          ),
        ),
      for (final e in _entries) _row(e),
      Align(
        alignment: Alignment.centerLeft,
        child: UiKitButton(
          key: const Key('settings-variable-add'),
          label: 'Add',
          style: UiKitButtonStyle.secondary,
          onPressed: _loading ? null : _add,
        ),
      ),
    ],
  );
}

/// Name, kind and value of a new entry. The value lives only in this
/// dialog's controller, which is cleared as soon as the save succeeds.
class _EntryDialog extends StatefulWidget {
  const _EntryDialog({required this.save, required this.describe});
  final Future<void> Function(String name, EntryKind kind, String value) save;
  final String Function(Object error) describe;
  @override
  State<_EntryDialog> createState() => _EntryDialogState();
}

class _EntryDialogState extends State<_EntryDialog> {
  final _name = TextEditingController();
  final _value = TextEditingController();
  EntryKind _kind = EntryKind.variable;
  bool _saving = false;
  String? _nameError, _error;

  @override
  void dispose() {
    _value.clear();
    _value.dispose();
    _name.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final name = _name.text.trim();
    if (!_validName.hasMatch(name)) {
      setState(() => _nameError = 'Use letters, digits, _ . - only');
      return;
    }
    if (_value.text.isEmpty) {
      setState(() {
        _nameError = null;
        _error = 'Enter a value first.';
      });
      return;
    }
    setState(() {
      _saving = true;
      _nameError = _error = null;
    });
    try {
      await widget.save(name, _kind, _value.text);
      if (!mounted) return;
      _value.clear();
      Navigator.pop(context, true);
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
  Widget build(BuildContext context) => PopScope(
    canPop: !_saving,
    child: SettingsDialog(
      title: 'Add a variable or secret',
      body: 'A variable can be read back. A secret is sealed and cannot.',
      field: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          UiKitField(
            label: 'Name',
            controller: _name,
            enabled: !_saving,
            error: _nameError,
            fieldKey: const Key('settings-variable-name'),
          ),
          const SizedBox(height: 12),
          UiKitSelect<EntryKind>(
            label: 'Kind',
            value: _kind,
            items: const [
              UiKitSelectItem(EntryKind.variable, 'Variable'),
              UiKitSelectItem(EntryKind.secret, 'Secret'),
            ],
            onChanged: _saving ? null : (k) => setState(() => _kind = k),
          ),
          const SizedBox(height: 12),
          UiKitField(
            label: 'Value',
            controller: _value,
            secret: _kind == EntryKind.secret,
            enabled: !_saving,
            error: _error,
            fieldKey: const Key('settings-variable-value'),
          ),
        ],
      ),
      actions: [
        UiKitButton(
          key: const Key('settings-variable-save'),
          label: _saving ? 'Saving…' : 'Save',
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
    ),
  );
}
