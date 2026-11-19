import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.RadioButtons.IRadioButtons`.
class RadioButtonsWidget extends StatelessWidget {
  const RadioButtonsWidget({
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
      kind: 'ui.radio-buttons',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final header = '${state['header'] ?? ''}';
        final selectedId = state['selectedId'] as String?;
        final isEnabled = state['isEnabled'] != false;
        final rawOptions = state['options'] is List
            ? state['options'] as List
            : const <dynamic>[];

        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            if (header.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: Text(
                  header,
                  style: TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                    color: c.textStrong,
                  ),
                ),
              ),
            for (final opt in rawOptions)
              if (opt is Map)
                InkWell(
                  borderRadius: BorderRadius.circular(8),
                  onTap: isEnabled
                      ? () => unawaited(invoke('Select', ['${opt['id']}']))
                      : null,
                  child: Padding(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 4,
                      vertical: 5,
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(
                          '${opt['id']}' == selectedId
                              ? Icons.radio_button_checked
                              : Icons.radio_button_off,
                          size: 18,
                          color: '${opt['id']}' == selectedId
                              ? c.accent
                              : c.textMuted,
                        ),
                        const SizedBox(width: 8),
                        Text(
                          '${opt['label'] ?? ''}',
                          style: TextStyle(
                            fontSize: 13.5,
                            color: isEnabled ? c.textBody : c.textMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
          ],
        );
      },
    );
  }
}
