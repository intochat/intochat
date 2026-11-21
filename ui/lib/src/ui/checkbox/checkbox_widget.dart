import 'dart:async';

import 'package:flutter/material.dart';

import '../mix/ui_mix_controls.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.CheckBox.ICheckBox`.
class CheckBoxWidget extends StatelessWidget {
  const CheckBoxWidget({
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
      kind: 'ui.checkbox',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final label = '${state['label'] ?? ''}';
        final isChecked = state['isChecked'] == true;
        final isEnabled = state['isEnabled'] != false;

        return Padding(
          padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 4),
          child: UiMixCheckbox(
            value: isChecked,
            label: label,
            enabled: isEnabled,
            semanticsId: neuronKey,
            onChanged: isEnabled ? (_) => unawaited(invoke('Toggle')) : null,
          ),
        );
      },
    );
  }
}
