import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Slider.ISlider`.
class SliderWidget extends StatelessWidget {
  const SliderWidget({
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
      kind: 'ui.slider',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final label = '${state['label'] ?? ''}';
        final min = (state['min'] as num?)?.toDouble() ?? 0.0;
        final max = (state['max'] as num?)?.toDouble() ?? 100.0;
        final step = (state['step'] as num?)?.toDouble() ?? 1.0;
        final value = ((state['value'] as num?)?.toDouble() ?? min).clamp(
          min,
          max,
        );
        final unit = '${state['unit'] ?? ''}';
        final isEnabled = state['isEnabled'] != false;
        final divisions = step > 0 && max > min
            ? ((max - min) / step).round().clamp(1, 200)
            : null;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Row(
              children: [
                if (label.isNotEmpty)
                  Expanded(
                    child: Text(
                      label,
                      style: TextStyle(
                        fontSize: 13.5,
                        fontWeight: FontWeight.w500,
                        color: isEnabled ? c.textStrong : c.textMuted,
                      ),
                    ),
                  ),
                Text(
                  '${value == value.roundToDouble() ? value.round() : value.toStringAsFixed(1)}$unit',
                  style: TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                    color: c.accent,
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ],
            ),
            Slider(
              value: value,
              min: min,
              max: max > min ? max : min + 1,
              divisions: divisions,
              onChanged: isEnabled
                  ? (v) => unawaited(invoke('SetValue', [v]))
                  : null,
            ),
          ],
        );
      },
    );
  }
}
