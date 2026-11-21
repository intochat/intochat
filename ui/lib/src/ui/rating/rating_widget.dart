import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../mix/ui_mix_controls.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Rating.IRating`.
class RatingWidget extends StatelessWidget {
  const RatingWidget({
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
      kind: 'ui.rating',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final label = '${state['label'] ?? ''}';
        final value = (state['value'] as num?)?.toInt() ?? 0;
        final max = (state['max'] as num?)?.toInt() ?? 5;
        final isEnabled = state['isEnabled'] != false;

        return Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (label.isNotEmpty) ...[
              Text(
                label,
                style: TextStyle(fontSize: 13.5, color: c.textStrong),
              ),
              const SizedBox(width: 10),
            ],
            for (var i = 1; i <= max; i++)
              UiMixIconButton(
                tooltip: '$i of $max',
                size: 28,
                icon: LucideIcons.star,
                iconSize: 20,
                iconColor: i <= value ? c.working : c.textMuted,
                enabled: isEnabled,
                onPressed: isEnabled
                    ? () => unawaited(invoke('SetValue', [i]))
                    : null,
              ),
            const SizedBox(width: 8),
            Text(
              '$value / $max',
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
