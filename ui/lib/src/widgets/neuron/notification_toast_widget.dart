import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../../primitives/primitives.dart';
import 'neuron_widget.dart';

/// Reactive WinUI 3 AppNotification / Toast presenter for `IntoChat.OS.Modules.UI.NotificationToast.INotificationToast`.
class NotificationToastNeuronWidget extends StatelessWidget {
  const NotificationToastNeuronWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.toast',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final isOpen = state['isOpen'] == true;
        final lastAction = state['lastInvokedActionId'] as String?;
        final rawItems = (state['items'] as List?) ?? const [];
        final items = <Map<String, dynamic>>[
          for (final item in rawItems)
            if (item is Map) Map<String, dynamic>.from(item),
        ];

        if (!isOpen || items.isEmpty) {
          return Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            decoration: BoxDecoration(
              color: c.surfaceSunk,
              borderRadius: BorderRadius.circular(IcRadius.sm),
              border: Border.all(color: c.borderHairline),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(LucideIcons.bell, size: 14, color: c.textMuted),
                const SizedBox(width: 8),
                Text(
                  lastAction != null
                      ? 'No active toasts Â· Last action: $lastAction'
                      : 'No active notifications',
                  style: TextStyle(fontSize: 12, color: c.textMuted),
                ),
              ],
            ),
          );
        }

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            for (var i = 0; i < items.length; i++) ...[
              if (i > 0) const SizedBox(height: 8),
              _ToastCard(
                item: items[i],
                colors: c,
                onAction: () => unawaited(
                  invoke('InvokeAction', ['${items[i]['id'] ?? ''}']),
                ),
                onDismiss: () =>
                    unawaited(invoke('Dismiss', ['${items[i]['id'] ?? ''}'])),
              ),
            ],
          ],
        );
      },
    );
  }
}

class _ToastCard extends StatelessWidget {
  const _ToastCard({
    required this.item,
    required this.colors,
    required this.onAction,
    required this.onDismiss,
  });

  final Map<String, dynamic> item;
  final IcColors colors;
  final VoidCallback onAction;
  final VoidCallback onDismiss;

  @override
  Widget build(BuildContext context) {
    final id = '${item['id'] ?? ''}';
    final title = '${item['title'] ?? 'Notification'}';
    final message = '${item['message'] ?? ''}';
    final severity = '${item['severity'] ?? 'info'}';
    final actionLabel = item['actionLabel'] as String?;

    final (Color accent, IconData icon) = switch (severity) {
      'success' => (colors.done, LucideIcons.circleCheck),
      'warning' => (colors.working, LucideIcons.triangleAlert),
      'error' => (colors.error, LucideIcons.circleX),
      _ => (colors.accent, LucideIcons.bell),
    };

    return Semantics(
      identifier: 'toast-item:$id',
      child: Container(
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          color: colors.surfaceElevated,
          borderRadius: BorderRadius.circular(IcRadius.card),
          border: Border.all(color: colors.borderHairline),
        ),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Container(
              width: 3,
              height: 36,
              decoration: BoxDecoration(
                color: accent,
                borderRadius: BorderRadius.circular(2),
              ),
            ),
            const SizedBox(width: 10),
            Icon(icon, size: 16, color: accent),
            const SizedBox(width: 10),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    title,
                    style: TextStyle(
                      fontSize: 12.5,
                      fontWeight: FontWeight.w600,
                      color: colors.textStrong,
                    ),
                  ),
                  if (message.isNotEmpty) ...[
                    const SizedBox(height: 2),
                    Text(
                      message,
                      style: TextStyle(fontSize: 12, color: colors.textBody),
                    ),
                  ],
                ],
              ),
            ),
            if (actionLabel != null && actionLabel.isNotEmpty) ...[
              const SizedBox(width: 8),
              TextButton(onPressed: onAction, child: Text(actionLabel)),
            ],
            UiMixIconButton(
              tooltip: 'Dismiss',
              size: 28,
              icon: LucideIcons.x,
              iconSize: 14,
              iconColor: colors.textMuted,
              onPressed: onDismiss,
            ),
          ],
        ),
      ),
    );
  }
}
