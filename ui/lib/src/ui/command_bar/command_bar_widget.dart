import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../../theme/ui_icon.dart';
import '../neuron_widget.dart';

class CommandBarNeuronWidget extends StatelessWidget {
  const CommandBarNeuronWidget({
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
      kind: 'ui.command-bar',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final items = ((state['items'] as List?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .toList();
        final primary = items.where((i) => i['primary'] != false).toList();
        final secondary = items.where((i) => i['primary'] == false).toList();
        final last = state['lastInvokedId'] as String?;

        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(color: c.borderHairline),
          ),
          child: Row(
            children: [
              ...primary.map((item) {
                final id = (item['id'] as String?) ?? '';
                final label = (item['label'] as String?) ?? id;
                final iconSlug = item['icon'] as String?;
                final disabled = item['disabled'] == true;
                return Padding(
                  padding: const EdgeInsets.only(right: 6),
                  child: TextButton.icon(
                    onPressed: disabled ? null : () => invoke('Invoke', [id]),
                    icon: Icon(uiIcon(iconSlug ?? 'zap'), size: 15),
                    label: Text(label, style: const TextStyle(fontSize: 12)),
                  ),
                );
              }),
              const Spacer(),
              if (last != null && last.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.only(right: 8),
                  child: Text(
                    'Last: $last',
                    style: TextStyle(fontSize: 11, color: c.textMuted),
                  ),
                ),
              if (secondary.isNotEmpty)
                PopupMenuButton<String>(
                  icon: Icon(Icons.more_horiz, size: 18, color: c.textBody),
                  onSelected: (id) => invoke('Invoke', [id]),
                  itemBuilder: (context) => secondary.map((item) {
                    final id = (item['id'] as String?) ?? '';
                    final label = (item['label'] as String?) ?? id;
                    final disabled = item['disabled'] == true;
                    return PopupMenuItem<String>(
                      value: id,
                      enabled: !disabled,
                      child: Text(label),
                    );
                  }).toList(),
                ),
            ],
          ),
        );
      },
    );
  }
}
