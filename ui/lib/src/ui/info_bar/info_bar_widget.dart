import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.InfoBar.IInfoBar`.
class InfoBarWidget extends StatelessWidget {
  const InfoBarWidget({
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
      kind: 'ui.info-bar',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        if (state['isOpen'] != true) {
          return const SizedBox.shrink();
        }
        final c = IcTheme.of(context);
        final title = '${state['title'] ?? ''}';
        final message = '${state['message'] ?? ''}';
        final severity = '${state['severity'] ?? 'info'}';
        final isClosable = state['isClosable'] != false;

        final (Color accent, IconData icon) = switch (severity) {
          'success' => (c.done, LucideIcons.circleCheck),
          'warning' => (c.working, LucideIcons.triangleAlert),
          'error' => (c.error, LucideIcons.circleX),
          _ => (c.accent, LucideIcons.info),
        };

        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
          decoration: BoxDecoration(
            color: accent.withValues(alpha: 0.10),
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(color: accent.withValues(alpha: 0.35)),
          ),
          child: Row(
            children: [
              Icon(icon, size: 17, color: accent),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    if (title.isNotEmpty)
                      Text(
                        title,
                        style: TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w700,
                          color: c.textStrong,
                        ),
                      ),
                    if (message.isNotEmpty)
                      Text(
                        message,
                        style: TextStyle(fontSize: 12.5, color: c.textBody),
                      ),
                  ],
                ),
              ),
              if (isClosable)
                IconButton(
                  tooltip: 'Dismiss',
                  icon: Icon(LucideIcons.x, size: 15, color: c.textMuted),
                  onPressed: () => unawaited(invoke('Dismiss')),
                ),
            ],
          ),
        );
      },
    );
  }
}
