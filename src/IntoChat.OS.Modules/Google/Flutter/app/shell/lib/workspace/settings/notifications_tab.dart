import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'settings_api.dart';
import 'settings_tab_heading.dart';

/// The preference ids the server knows, with their labels and defaults.
const notificationPreferences = <(String, String, bool)>[
  ('agent-finished', 'An agent finishes a task', true),
  ('needs-approval', 'Something needs your approval', true),
  ('connection-errors', 'A connection stops working', true),
  ('product-updates', 'Product updates', false),
];

/// Four switches. A change shows at once, is saved in the background, and is
/// put back with a toast when the save fails.
class NotificationsTab extends StatefulWidget {
  const NotificationsTab({super.key, required this.api});
  final SettingsApi api;
  @override
  State<NotificationsTab> createState() => _NotificationsTabState();
}

enum _Save { idle, saving, saved }

class _NotificationsTabState extends State<NotificationsTab> {
  final _values = {for (final p in notificationPreferences) p.$1: p.$3};
  final _state = <String, _Save>{};
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final stored = await widget.api.notifications();
      if (mounted) {
        setState(() {
          for (final e in stored.entries) {
            if (_values.containsKey(e.key)) _values[e.key] = e.value;
          }
        });
      }
    } catch (_) {
      if (mounted) {
        setState(() => _error = 'Notifications could not be loaded.');
      }
    }
  }

  Future<void> _toggle(String id, bool next) async {
    final before = _values[id]!;
    setState(() {
      _values[id] = next;
      _state[id] = _Save.saving;
    });
    try {
      await widget.api.putNotifications({id: next});
      if (mounted) setState(() => _state[id] = _Save.saved);
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _values[id] = before;
        _state[id] = _Save.idle;
      });
      showKitToast(context, 'Could not save that change.', tone: UiTone.error);
    }
  }

  Widget _status(String id) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    return switch (_state[id] ?? _Save.idle) {
      _Save.saving => Text('Saving…', style: type.meta),
      _Save.saved => Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.check, size: 12, color: c.done),
          const SizedBox(width: 4),
          Text('Saved', style: type.metaStrong),
        ],
      ),
      _Save.idle => const SizedBox.shrink(),
    };
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SettingsTabHeading(
          title: 'Notifications',
          description: 'Choose what this brain tells you about.',
        ),
        if (_error != null)
          Padding(
            padding: const EdgeInsets.only(bottom: 12),
            child: Semantics(liveRegion: true, child: Text(_error!)),
          ),
        for (final (id, label, _) in notificationPreferences)
          Container(
            margin: const EdgeInsets.only(bottom: 8),
            padding: const EdgeInsets.all(11),
            decoration: BoxDecoration(
              color: c.surface,
              border: Border.all(color: c.hairline),
              borderRadius: BorderRadius.circular(
                UiTokens.of(context).controlRadius,
              ),
            ),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    label,
                    style: type.body.copyWith(color: c.strong),
                  ),
                ),
                _status(id),
                const SizedBox(width: 12),
                UiKitSwitch(
                  key: Key('settings-notification-$id'),
                  value: _values[id]!,
                  semanticLabel: label,
                  onChanged: (v) => _toggle(id, v),
                ),
              ],
            ),
          ),
      ],
    );
  }
}
