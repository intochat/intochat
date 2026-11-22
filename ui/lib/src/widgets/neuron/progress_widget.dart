import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import 'neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Progress.IProgressIndicator`.
class ProgressWidget extends StatelessWidget {
  const ProgressWidget({
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
      kind: 'ui.progress',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final label = '${state['label'] ?? ''}';
        final value = ((state['value'] as num?)?.toDouble() ?? 0.0).clamp(
          0.0,
          1.0,
        );
        final indeterminate = state['isIndeterminate'] == true;
        final isRing = '${state['kind'] ?? 'bar'}' == 'ring';

        if (isRing) {
          return Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              SizedBox(
                width: 22,
                height: 22,
                child: CircularProgressIndicator(
                  value: indeterminate ? null : value,
                  strokeWidth: 2.5,
                ),
              ),
              if (label.isNotEmpty) ...[
                const SizedBox(width: 10),
                Text(label, style: TextStyle(fontSize: 13, color: c.textBody)),
              ],
            ],
          );
        }

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          spacing: 6,
          children: [
            if (label.isNotEmpty)
              Row(
                children: [
                  Expanded(
                    child: Text(
                      label,
                      style: TextStyle(fontSize: 13, color: c.textStrong),
                    ),
                  ),
                  if (!indeterminate)
                    Text(
                      '${(value * 100).round()}%',
                      style: TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.w600,
                        color: c.textMuted,
                        fontFeatures: const [FontFeature.tabularFigures()],
                      ),
                    ),
                ],
              ),
            ClipRRect(
              borderRadius: BorderRadius.circular(999),
              child: LinearProgressIndicator(
                value: indeterminate ? null : value,
                minHeight: 6,
              ),
            ),
          ],
        );
      },
    );
  }
}
