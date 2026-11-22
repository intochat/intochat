import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../../theme/ui_icon.dart';
import 'neuron_widget.dart';

class ContextMenuNeuronWidget extends StatelessWidget {
  const ContextMenuNeuronWidget({
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
      kind: 'ui.context-menu',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final trigger = (state['triggerLabel'] as String?) ?? 'Actions';
        final last = state['lastInvokedItemId'] as String?;
        final items = ((state['items'] as List?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .toList();

        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(color: c.borderHairline),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              PopupMenuButton<String>(
                onSelected: (id) => invoke('Invoke', [id]),
                itemBuilder: (context) => items.map((item) {
                  final id = (item['id'] as String?) ?? '';
                  final label = (item['label'] as String?) ?? id;
                  final shortcut = item['shortcut'] as String?;
                  final iconSlug = item['icon'] as String?;
                  final destructive = item['destructive'] == true;
                  final disabled = item['disabled'] == true;
                  return PopupMenuItem<String>(
                    value: id,
                    enabled: !disabled,
                    child: Row(
                      children: [
                        if (iconSlug != null) ...[
                          Icon(
                            uiIcon(iconSlug),
                            size: 15,
                            color: destructive ? c.error : c.textBody,
                          ),
                          const SizedBox(width: 8),
                        ],
                        Expanded(
                          child: Text(
                            label,
                            style: TextStyle(
                              fontSize: 13,
                              color: destructive ? c.error : c.textStrong,
                            ),
                          ),
                        ),
                        if (shortcut != null)
                          Text(
                            shortcut,
                            style: TextStyle(fontSize: 11, color: c.textMuted),
                          ),
                      ],
                    ),
                  );
                }).toList(),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(Icons.more_horiz, size: 16, color: c.accent),
                    const SizedBox(width: 6),
                    Text(
                      trigger,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w500,
                        color: c.textStrong,
                      ),
                    ),
                  ],
                ),
              ),
              if (last != null && last.isNotEmpty) ...[
                const SizedBox(width: 12),
                Text(
                  'Last: $last',
                  style: TextStyle(fontSize: 11, color: c.textMuted),
                ),
              ],
            ],
          ),
        );
      },
    );
  }
}
