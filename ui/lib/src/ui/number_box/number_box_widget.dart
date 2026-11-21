import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../mix/ui_mix_controls.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.NumberBox.INumberBox`.
class NumberBoxWidget extends StatelessWidget {
  const NumberBoxWidget({
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
      kind: 'ui.number-box',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final label = '${state['label'] ?? ''}';
        final value = (state['value'] as num?)?.toDouble() ?? 0.0;
        final prefix = '${state['prefix'] ?? ''}';
        final isEnabled = state['isEnabled'] != false;
        final formatted = value == value.roundToDouble()
            ? '${value.round()}'
            : value.toStringAsFixed(2);

        return Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (label.isNotEmpty) ...[
              Text(
                label,
                style: TextStyle(
                  fontSize: 13.5,
                  fontWeight: FontWeight.w500,
                  color: isEnabled ? c.textStrong : c.textMuted,
                ),
              ),
              const SizedBox(width: 12),
            ],
            Container(
              decoration: BoxDecoration(
                color: c.surfaceCard,
                borderRadius: BorderRadius.circular(IcRadius.sm),
                border: Border.all(color: c.borderInput),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  UiMixIconButton(
                    tooltip: 'Decrement',
                    icon: LucideIcons.minus,
                    iconSize: 15,
                    iconColor: c.textBody,
                    enabled: isEnabled,
                    onPressed: isEnabled
                        ? () => unawaited(invoke('Decrement'))
                        : null,
                  ),
                  Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 10),
                    child: Text(
                      '$prefix$formatted',
                      style: TextStyle(
                        fontSize: 14,
                        fontWeight: FontWeight.w600,
                        color: c.textStrong,
                        fontFeatures: const [FontFeature.tabularFigures()],
                      ),
                    ),
                  ),
                  UiMixIconButton(
                    tooltip: 'Increment',
                    icon: LucideIcons.plus,
                    iconSize: 15,
                    iconColor: c.textBody,
                    enabled: isEnabled,
                    onPressed: isEnabled
                        ? () => unawaited(invoke('Increment'))
                        : null,
                  ),
                ],
              ),
            ),
          ],
        );
      },
    );
  }
}
