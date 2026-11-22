import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../../primitives/primitives.dart';
import 'neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Slider.ISlider`.
///
/// Uses optimistic local drag state for 120fps visual responsiveness and
/// commits the final value via `SetValue` on drag end (preventing per-pixel
/// RPC flooding).
class SliderWidget extends StatefulWidget {
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
  State<SliderWidget> createState() => _SliderWidgetState();
}

class _SliderWidgetState extends State<SliderWidget> {
  double? _draftValue;

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.slider',
      neuronKey: widget.neuronKey,
      callNeuron: widget.callNeuron,
      watch: widget.watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final label = '${state['label'] ?? ''}';
        final min = (state['min'] as num?)?.toDouble() ?? 0.0;
        final max = (state['max'] as num?)?.toDouble() ?? 100.0;
        final step = (state['step'] as num?)?.toDouble() ?? 1.0;
        final serverValue = ((state['value'] as num?)?.toDouble() ?? min).clamp(
          min,
          max,
        );
        final value = (_draftValue ?? serverValue).clamp(min, max);
        final unit = '${state['unit'] ?? ''}';
        final isEnabled = state['isEnabled'] != false;

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
            UiMixSlider(
              value: value,
              min: min,
              max: max > min ? max : min + 1,
              step: step > 0 ? step : 1.0,
              enabled: isEnabled,
              semanticsId: widget.neuronKey,
              onChanged: isEnabled
                  ? (v) => setState(() => _draftValue = v)
                  : null,
              onChangeEnd: isEnabled
                  ? (v) {
                      setState(() => _draftValue = null);
                      unawaited(invoke('SetValue', [v]));
                    }
                  : null,
            ),
          ],
        );
      },
    );
  }
}
