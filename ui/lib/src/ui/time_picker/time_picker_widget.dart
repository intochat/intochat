import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

class TimePickerNeuronWidget extends StatelessWidget {
  const TimePickerNeuronWidget({
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
      kind: 'ui.time-picker',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final label = (state['label'] as String?) ?? 'Time';
        final hour = (state['hour'] as num?)?.toInt() ?? 9;
        final minute = (state['minute'] as num?)?.toInt() ?? 0;
        final disabled = state['disabled'] == true;
        final formatted =
            '${hour.toString().padLeft(2, '0')}:${minute.toString().padLeft(2, '0')}';

        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(color: c.borderHairline),
          ),
          child: Row(
            children: [
              Icon(Icons.schedule_outlined, size: 16, color: c.accent),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      label,
                      style: TextStyle(fontSize: 11, color: c.textMuted),
                    ),
                    Text(
                      formatted,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                  ],
                ),
              ),
              TextButton(
                onPressed: disabled
                    ? null
                    : () async {
                        final picked = await showTimePicker(
                          context: context,
                          initialTime: TimeOfDay(hour: hour, minute: minute),
                        );
                        if (picked != null) {
                          invoke('SetTime', [picked.hour, picked.minute]);
                        }
                      },
                child: const Text('Set'),
              ),
            ],
          ),
        );
      },
    );
  }
}
