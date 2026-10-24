import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'settings_api.dart';
import 'settings_tab_heading.dart';

/// Archived chats, newest first, each with an Unarchive button. Titles are
/// customer data and render as plain [Text]: no markup is interpreted.
class ArchivedTab extends StatefulWidget {
  const ArchivedTab({super.key, required this.api});
  final SettingsApi api;
  @override
  State<ArchivedTab> createState() => _ArchivedTabState();
}

class _ArchivedTabState extends State<ArchivedTab> {
  List<ArchivedChat> _chats = const [];
  bool _loading = true;
  String? _error;
  final _busy = <String>{};

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final chats = await widget.api.archivedChats();
      if (mounted) setState(() => _chats = chats);
    } catch (_) {
      if (mounted) {
        setState(() => _error = 'Archived chats could not be loaded.');
      }
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _unarchive(ArchivedChat chat) async {
    setState(() => _busy.add(chat.id));
    try {
      await widget.api.unarchiveChat(chat.id);
      if (!mounted) return;
      setState(
        () => _chats = [
          for (final c in _chats)
            if (c.id != chat.id) c,
        ],
      );
      showKitToast(context, 'Chat restored.');
    } catch (error) {
      if (mounted) {
        showKitToast(
          context,
          error is SettingsForbidden
              ? error.toString()
              : 'That chat could not be restored. Try again.',
          tone: UiTone.error,
        );
      }
    } finally {
      if (mounted) setState(() => _busy.remove(chat.id));
    }
  }

  static String _ago(DateTime at) {
    final age = DateTime.now().difference(at);
    if (age.inMinutes < 1) return 'Just now';
    if (age.inHours < 1) return '${age.inMinutes} min ago';
    if (age.inDays < 1) return '${age.inHours} h ago';
    if (age.inDays < 30) return '${age.inDays} d ago';
    return '${at.year}-${at.month.toString().padLeft(2, '0')}-'
        '${at.day.toString().padLeft(2, '0')}';
  }

  Widget _row(ArchivedChat chat) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    return Container(
      key: Key('archived-row-${chat.id}'),
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
                  chat.title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: type.body.copyWith(
                    color: c.strong,
                    fontWeight: FontWeight.w600,
                  ),
                ),
                const SizedBox(height: 2),
                Text(_ago(chat.updatedAt), style: type.meta),
              ],
            ),
          ),
          const SizedBox(width: 8),
          UiKitButton(
            key: Key('archived-unarchive-${chat.id}'),
            label: 'Unarchive',
            style: UiKitButtonStyle.secondary,
            compact: true,
            onPressed: _busy.contains(chat.id) ? null : () => _unarchive(chat),
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
        title: 'Archived chats',
        description: 'Conversations you have archived.',
      ),
      if (_loading) const LinearProgressIndicator(),
      if (_error != null) Semantics(liveRegion: true, child: Text(_error!)),
      if (!_loading && _error == null && _chats.isEmpty)
        Text(
          'Archived chats appear here.',
          style: UiType.of(context).bodyMuted,
        ),
      for (final chat in _chats) _row(chat),
    ],
  );
}
