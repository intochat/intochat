import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../../theme/ui_icon.dart';
import 'neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.ListBox.IListBox`.
class ListBoxWidget extends StatelessWidget {
  const ListBoxWidget({
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
      kind: 'ui.listbox',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final selectedId = state['selectedId'] as String?;
        final isEnabled = state['isEnabled'] != false;
        final rawItems = state['items'] is List
            ? state['items'] as List
            : const <dynamic>[];

        String? lastGroup;
        final children = <Widget>[];
        for (final raw in rawItems) {
          if (raw is! Map) continue;
          final id = '${raw['id'] ?? ''}';
          final label = '${raw['label'] ?? ''}';
          final subtitle = raw['subtitle'] as String?;
          final group = raw['group'] as String?;
          final icon = raw['icon'] as String?;
          final selected = id == selectedId;

          if (group != null && group.isNotEmpty && group != lastGroup) {
            lastGroup = group;
            children.add(
              Padding(
                padding: const EdgeInsets.fromLTRB(10, 10, 10, 4),
                child: Text(
                  group.toUpperCase(),
                  style: TextStyle(
                    fontSize: 10.5,
                    fontWeight: FontWeight.w700,
                    letterSpacing: 0.7,
                    color: c.textMuted,
                  ),
                ),
              ),
            );
          }

          children.add(
            Material(
              color: selected
                  ? c.accent.withValues(alpha: 0.12)
                  : Colors.transparent,
              borderRadius: BorderRadius.circular(IcRadius.sm),
              child: InkWell(
                borderRadius: BorderRadius.circular(IcRadius.sm),
                onTap: isEnabled
                    ? () => unawaited(invoke('Select', [id]))
                    : null,
                child: Padding(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 10,
                    vertical: 8,
                  ),
                  child: Row(
                    children: [
                      if (icon != null && icon.isNotEmpty) ...[
                        Icon(
                          uiIcon(icon),
                          size: 16,
                          color: selected ? c.accent : c.textMuted,
                        ),
                        const SizedBox(width: 10),
                      ],
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            Text(
                              label,
                              style: TextStyle(
                                fontSize: 13,
                                fontWeight: selected
                                    ? FontWeight.w600
                                    : FontWeight.w500,
                                color: selected ? c.accent : c.textStrong,
                              ),
                            ),
                            if (subtitle != null && subtitle.isNotEmpty)
                              Text(
                                subtitle,
                                style: TextStyle(
                                  fontSize: 11.5,
                                  color: c.textMuted,
                                ),
                              ),
                          ],
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          );
        }

        return Container(
          padding: const EdgeInsets.all(6),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            children: children,
          ),
        );
      },
    );
  }
}
