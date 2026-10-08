import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

/// Renders a chat projection; input and execution belong to composed neurons.
class UiChatTranscript extends StatefulWidget {
  const UiChatTranscript({
    super.key,
    required this.messages,
    required this.markdown,
  });

  final List<Map<String, dynamic>> messages;
  final Widget Function(String) markdown;

  @override
  State<UiChatTranscript> createState() => _UiChatTranscriptState();
}

class _UiChatTranscriptState extends State<UiChatTranscript> {
  final _scroll = ScrollController();
  bool _following = true;

  @override
  void initState() {
    super.initState();
    _scroll.addListener(_positionChanged);
    _follow();
  }

  @override
  void didUpdateWidget(UiChatTranscript oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (_following) _follow();
  }

  void _positionChanged() {
    final following = _scroll.position.extentAfter < 64;
    if (following != _following) setState(() => _following = following);
  }

  void _follow() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && _scroll.hasClients) {
        _scroll.jumpTo(_scroll.position.maxScrollExtent);
      }
    });
  }

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (widget.messages.isEmpty) {
      return const Center(
        child: Padding(
          padding: EdgeInsets.all(24),
          child: Text('Start a conversation', textAlign: TextAlign.center),
        ),
      );
    }
    return Stack(
      children: [
        Positioned.fill(
          child: Scrollbar(
            controller: _scroll,
            child: SingleChildScrollView(
              controller: _scroll,
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 56),
              child: Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 760),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      for (final message in widget.messages)
                        _entry(context, message),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
        if (!_following)
          Positioned(
            bottom: 12,
            left: 0,
            right: 0,
            child: Center(
              child: FilledButton.tonalIcon(
                onPressed: () {
                  setState(() => _following = true);
                  _follow();
                },
                icon: const Icon(Icons.arrow_downward, size: 16),
                label: const Text('Latest message'),
              ),
            ),
          ),
      ],
    );
  }

  Widget _entry(BuildContext context, Map<String, dynamic> message) {
    final theme = Theme.of(context);
    final user =
        message['role'] == 0 ||
        message['role'] == 'user' ||
        message['role'] == 'User';
    final text = message['text'] as String? ?? '';
    final status = message['status'] as String? ?? 'completed';
    final error = message['error'] as String?;
    final activities = (message['activities'] as List? ?? const [])
        .whereType<Map>()
        .toList();
    final failedActivities = activities
        .where((item) => item['error'] != null)
        .length;
    return Padding(
      key: ValueKey(message['id']),
      padding: const EdgeInsets.only(bottom: 24),
      child: Align(
        alignment: user ? Alignment.centerRight : Alignment.centerLeft,
        child: Container(
          width: double.infinity,
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: user
                ? theme.colorScheme.surfaceContainerHigh
                : theme.colorScheme.surface,
            borderRadius: BorderRadius.circular(16),
            border: user
                ? null
                : Border.all(color: theme.colorScheme.outlineVariant),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      user ? 'You' : 'Assistant',
                      style: theme.textTheme.labelLarge,
                    ),
                  ),
                  if (text.isNotEmpty)
                    IconButton(
                      tooltip: 'Copy message',
                      visualDensity: VisualDensity.compact,
                      icon: const Icon(Icons.copy_outlined, size: 16),
                      onPressed: () =>
                          Clipboard.setData(ClipboardData(text: text)),
                    ),
                ],
              ),
              if (activities.isNotEmpty)
                Material(
                  type: MaterialType.transparency,
                  child: ExpansionTile(
                    key: PageStorageKey('${message['id']}/activities'),
                    tilePadding: EdgeInsets.zero,
                    leading: Icon(
                      failedActivities > 0
                          ? Icons.error_outline
                          : Icons.history,
                      size: 18,
                    ),
                    title: Text(
                      '${activities.length} ${activities.length == 1 ? 'activity' : 'activities'}'
                      '${failedActivities > 0 ? ' · $failedActivities failed' : ''}',
                    ),
                    children: [
                      for (final activity in activities)
                        Padding(
                          padding: const EdgeInsets.only(top: 8),
                          child: Material(
                            type: MaterialType.transparency,
                            child: ExpansionTile(
                              key: PageStorageKey(
                                '${message['id']}/${activity['id']}',
                              ),
                              tilePadding: const EdgeInsets.symmetric(
                                horizontal: 8,
                              ),
                              leading: Icon(
                                activity['error'] != null
                                    ? Icons.error_outline
                                    : activity['result'] != null
                                    ? Icons.check_circle_outline
                                    : Icons.hourglass_empty,
                                size: 18,
                              ),
                              title: Text(
                                '${activity['title'] ?? 'Activity'}',
                                maxLines: 2,
                                overflow: TextOverflow.ellipsis,
                              ),
                              children: [
                                ConstrainedBox(
                                  constraints: const BoxConstraints(
                                    maxHeight: 260,
                                  ),
                                  child: SingleChildScrollView(
                                    key: PageStorageKey(
                                      '${message['id']}/${activity['id']}/details',
                                    ),
                                    padding: const EdgeInsets.all(12),
                                    child: Column(
                                      crossAxisAlignment:
                                          CrossAxisAlignment.stretch,
                                      children: [
                                        const Text('Input'),
                                        SelectableText(
                                          '${activity['input'] ?? ''}',
                                          style: theme.textTheme.bodySmall,
                                        ),
                                        if (activity['result'] != null) ...[
                                          const SizedBox(height: 12),
                                          const Text('Result'),
                                          SelectableText(
                                            '${activity['result']}',
                                            style: theme.textTheme.bodySmall,
                                          ),
                                        ],
                                        if (activity['error'] != null) ...[
                                          const SizedBox(height: 12),
                                          const Text('Error'),
                                          SelectableText(
                                            '${activity['error']}',
                                            style: TextStyle(
                                              color: theme.colorScheme.error,
                                            ),
                                          ),
                                        ],
                                      ],
                                    ),
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
              if (text.isNotEmpty) widget.markdown(text),
              if (status == 'running')
                const Padding(
                  padding: EdgeInsets.only(top: 12),
                  child: Text('Responding…'),
                ),
              if (status != 'running' && status != 'completed')
                Padding(
                  padding: const EdgeInsets.only(top: 12),
                  child: Text(
                    error ??
                        switch (status) {
                          'cancelled' => 'Response stopped.',
                          'interrupted' => 'Response interrupted. You can send another message.',
                          _ => 'Response failed. You can try again.',
                        },
                    style: TextStyle(color: theme.colorScheme.error),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}
