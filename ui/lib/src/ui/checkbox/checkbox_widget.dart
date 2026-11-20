import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
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
        final c = IcTheme.of(context);
        final label = '${state['label'] ?? ''}';
        final isChecked = state['isChecked'] == true;
        final isEnabled = state['isEnabled'] != false;

        return InkWell(
          borderRadius: BorderRadius.circular(8),
          onTap: isEnabled ? () => unawaited(invoke('Toggle')) : null,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 4),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Checkbox(
                  value: isChecked,
                  onChanged: isEnabled
                      ? (_) => unawaited(invoke('Toggle'))
                      : null,
                  visualDensity: VisualDensity.compact,
                ),
                if (label.isNotEmpty) ...[
                  const SizedBox(width: 6),
                  Flexible(
                    child: Text(
                      label,
                      style: TextStyle(
                        fontSize: 13.5,
                        color: isEnabled ? c.textStrong : c.textMuted,
                      ),
                    ),
                  ),
                ],
              ],
            ),
          ),
        );
      },
    );
  }
}
