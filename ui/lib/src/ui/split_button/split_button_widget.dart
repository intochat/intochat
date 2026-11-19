import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../../theme/ui_icon.dart';
import '../neuron_widget.dart';

class SplitButtonNeuronWidget extends StatelessWidget {
  const SplitButtonNeuronWidget({
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
      kind: 'ui.split-button',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final label = (state['primaryLabel'] as String?) ?? 'Run';
        final iconSlug = state['primaryIcon'] as String?;
        final count = (state['invokeCount'] as num?)?.toInt() ?? 0;
        final options = ((state['options'] as List?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .toList();

        return Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              decoration: BoxDecoration(
                color: c.accent,
                borderRadius: BorderRadius.circular(IcRadius.sm),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  InkWell(
                    onTap: () => invoke('InvokePrimary', const []),
                    child: Padding(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 12,
                        vertical: 8,
                      ),
                      child: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          if (iconSlug != null) ...[
                            Icon(
                              uiIcon(iconSlug),
                              size: 15,
                              color: Colors.white,
                            ),
                            const SizedBox(width: 6),
                          ],
                          Text(
                            label,
                            style: const TextStyle(
                              fontSize: 12.5,
                              fontWeight: FontWeight.w600,
                              color: Colors.white,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                  Container(
                    width: 1,
                    height: 22,
                    color: Colors.white.withValues(alpha: 0.3),
                  ),
                  PopupMenuButton<String>(
                    icon: const Icon(
                      Icons.keyboard_arrow_down,
                      size: 18,
                      color: Colors.white,
                    ),
                    onSelected: (id) => invoke('SelectOption', [id]),
                    itemBuilder: (context) => options.map((opt) {
                      final id = (opt['id'] as String?) ?? '';
                      final optLabel = (opt['label'] as String?) ?? id;
                      return PopupMenuItem<String>(
                        value: id,
                        child: Text(optLabel),
                      );
                    }).toList(),
                  ),
                ],
              ),
            ),
            if (count > 0) ...[
              const SizedBox(width: 10),
              Text(
                'Runs: $count',
                style: TextStyle(fontSize: 11, color: c.textMuted),
              ),
            ],
          ],
        );
      },
    );
  }
}
