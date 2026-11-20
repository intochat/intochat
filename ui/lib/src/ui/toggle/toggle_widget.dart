import 'dart:async';

import 'package:flutter/material.dart';

import '../../components/kit/ui_kit_switch.dart';
import '../../design/tokens.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Toggle.IToggle`.
class ToggleWidget extends StatelessWidget {
  const ToggleWidget({
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
      kind: 'ui.toggle',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final label = '${state['label'] ?? ''}';
        final isOn = state['isOn'] == true;
        final isEnabled = state['isEnabled'] != false;
        final stateText = isOn
            ? '${state['onLabel'] ?? 'On'}'
            : '${state['offLabel'] ?? 'Off'}';

        return Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (label.isNotEmpty) ...[
              Flexible(
                child: Text(
                  label,
                  style: TextStyle(
                    fontSize: 13.5,
                    color: isEnabled ? c.textStrong : c.textMuted,
                  ),
                ),
              ),
              const SizedBox(width: 10),
            ],
            UiKitSwitch(
              value: isOn,
              locked: !isEnabled,
              semanticLabel: label.isEmpty ? neuronKey : label,
              onChanged: isEnabled ? (_) => unawaited(invoke('Toggle')) : null,
            ),
            const SizedBox(width: 8),
            Text(
              stateText,
              style: TextStyle(
                fontSize: 12,
                color: c.textMuted,
                fontFeatures: const [FontFeature.tabularFigures()],
              ),
            ),
          ],
        );
      },
    );
  }
}
