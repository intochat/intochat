import 'dart:async';

import 'package:flutter/material.dart';

import '../../components/kit/ui_kit_select.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Select.ISelect`.
class SelectWidget extends StatelessWidget {
  const SelectWidget({
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
      kind: 'ui.select',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final label = '${state['label'] ?? ''}';
        final selectedId = state['selectedId'] as String?;
        final isEnabled = state['isEnabled'] != false;
        final rawOptions = state['options'] is List
            ? state['options'] as List
            : const <dynamic>[];
        final items = <UiKitSelectItem<String>>[
          for (final opt in rawOptions)
            if (opt is Map)
              UiKitSelectItem<String>(
                '${opt['id'] ?? ''}',
                '${opt['label'] ?? ''}',
                detail: opt['subtitle'] as String?,
              ),
        ];

        return UiKitSelect<String>(
          label: label.isEmpty ? null : label,
          value: selectedId,
          items: items,
          onChanged: isEnabled
              ? (id) => unawaited(invoke('Select', [id]))
              : null,
        );
      },
    );
  }
}
