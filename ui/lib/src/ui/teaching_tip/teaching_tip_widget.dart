import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

class TeachingTipNeuronWidget extends StatelessWidget {
  const TeachingTipNeuronWidget({
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
      kind: 'ui.teaching-tip',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final isOpen = state['isOpen'] == true;
        final title = (state['title'] as String?) ?? 'Tip';
        final subtitle = (state['subtitle'] as String?) ?? '';
        final actionLabel = state['actionLabel'] as String?;

        if (!isOpen) {
          return OutlinedButton.icon(
            onPressed: () => invoke('Show', [title, subtitle, actionLabel]),
            icon: const Icon(Icons.lightbulb_outline, size: 15),
            label: Text(
              'Show tip: $title',
              style: const TextStyle(fontSize: 12),
            ),
          );
        }

        return Container(
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            color: c.surfaceElevated,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.accent.withValues(alpha: 0.45)),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(Icons.lightbulb_outline, size: 16, color: c.accent),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      title,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                  ),
                  IconButton(
                    icon: const Icon(Icons.close, size: 16),
                    onPressed: () => invoke('Dismiss', const []),
                  ),
                ],
              ),
              if (subtitle.isNotEmpty) ...[
                const SizedBox(height: 6),
                Text(
                  subtitle,
                  style: TextStyle(fontSize: 12, color: c.textBody),
                ),
              ],
              const SizedBox(height: 10),
              Row(
                children: [
                  if (actionLabel != null && actionLabel.isNotEmpty)
                    FilledButton(
                      onPressed: () => invoke('InvokeAction', const []),
                      child: Text(
                        actionLabel,
                        style: const TextStyle(fontSize: 12),
                      ),
                    ),
                  const SizedBox(width: 8),
                  TextButton(
                    onPressed: () => invoke('Dismiss', const []),
                    child: const Text('Got it', style: TextStyle(fontSize: 12)),
                  ),
                ],
              ),
            ],
          ),
        );
      },
    );
  }
}
