import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../../primitives/primitives.dart';
import 'neuron_widget.dart';

class CalendarViewNeuronWidget extends StatelessWidget {
  const CalendarViewNeuronWidget({
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
      kind: 'ui.calendar',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = (state['title'] as String?) ?? 'Calendar';
        final year = (state['year'] as num?)?.toInt() ?? 2026;
        final month = (state['month'] as num?)?.toInt() ?? 11;
        final selected = ((state['selectedDates'] as List?) ?? const [])
            .map((e) => e.toString())
            .toSet();
        final daysInMonth = DateUtils.getDaysInMonth(year, month);

        return Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      '$title · $year-${month.toString().padLeft(2, '0')}',
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                  ),
                  UiMixIconButton(
                    tooltip: 'Previous month',
                    icon: Icons.chevron_left,
                    iconSize: 18,
                    onPressed: () {
                      final prevMonth = month == 1 ? 12 : month - 1;
                      final prevYear = month == 1 ? year - 1 : year;
                      invoke('NavigateMonth', [prevYear, prevMonth]);
                    },
                  ),
                  UiMixIconButton(
                    tooltip: 'Next month',
                    icon: Icons.chevron_right,
                    iconSize: 18,
                    onPressed: () {
                      final nextMonth = month == 12 ? 1 : month + 1;
                      final nextYear = month == 12 ? year + 1 : year;
                      invoke('NavigateMonth', [nextYear, nextMonth]);
                    },
                  ),
                ],
              ),
              const SizedBox(height: 8),
              Wrap(
                spacing: 4,
                runSpacing: 4,
                children: List.generate(daysInMonth, (index) {
                  final day = index + 1;
                  final iso =
                      '${year.toString().padLeft(4, '0')}-${month.toString().padLeft(2, '0')}-${day.toString().padLeft(2, '0')}';
                  final isSelected = selected.contains(iso);
                  return InkWell(
                    borderRadius: BorderRadius.circular(6),
                    onTap: () => invoke('ToggleDate', [iso]),
                    child: Container(
                      width: 32,
                      height: 30,
                      alignment: Alignment.center,
                      decoration: BoxDecoration(
                        color: isSelected ? c.accent : c.surfaceSunk,
                        borderRadius: BorderRadius.circular(6),
                      ),
                      child: Text(
                        '$day',
                        style: TextStyle(
                          fontSize: 12,
                          fontWeight: isSelected
                              ? FontWeight.w600
                              : FontWeight.w400,
                          color: isSelected ? Colors.white : c.textBody,
                        ),
                      ),
                    ),
                  );
                }),
              ),
            ],
          ),
        );
      },
    );
  }
}
