import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import 'neuron_widget.dart';

class DatePickerNeuronWidget extends StatelessWidget {
  const DatePickerNeuronWidget({
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
      kind: 'ui.date-picker',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final label = (state['label'] as String?) ?? 'Date';
        final selected = (state['selectedDate'] as String?) ?? '2026-11-19';
        final disabled = state['disabled'] == true;
        final parsed = DateTime.tryParse(selected) ?? DateTime(2026, 11, 19);

        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(color: c.borderHairline),
          ),
          child: Row(
            children: [
              Icon(Icons.calendar_today_outlined, size: 16, color: c.accent),
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
                      selected,
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
                        final picked = await showDatePicker(
                          context: context,
                          initialDate: parsed,
                          firstDate: DateTime(1970),
                          lastDate: DateTime(2100),
                        );
                        if (picked != null) {
                          final iso =
                              '${picked.year.toString().padLeft(4, '0')}-${picked.month.toString().padLeft(2, '0')}-${picked.day.toString().padLeft(2, '0')}';
                          invoke('SetDate', [iso]);
                        }
                      },
                child: const Text('Pick'),
              ),
            ],
          ),
        );
      },
    );
  }
}
