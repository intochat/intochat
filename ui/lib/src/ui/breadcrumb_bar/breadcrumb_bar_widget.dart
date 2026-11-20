import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.BreadcrumbBar.IBreadcrumbBar`.
class BreadcrumbBarWidget extends StatelessWidget {
  const BreadcrumbBarWidget({
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
      kind: 'ui.breadcrumb',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final rawItems = state['items'] is List
            ? state['items'] as List
            : const <dynamic>[];
        final items = <(String id, String label)>[
          for (final item in rawItems)
            if (item is Map) ('${item['id'] ?? ''}', '${item['label'] ?? ''}'),
        ];

        return Wrap(
          spacing: 4,
          runSpacing: 4,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            for (var i = 0; i < items.length; i++) ...[
              if (i > 0)
                Icon(LucideIcons.chevronRight, size: 14, color: c.textMuted),
              InkWell(
                borderRadius: BorderRadius.circular(6),
                onTap: i == items.length - 1
                    ? null
                    : () => unawaited(invoke('NavigateTo', [items[i].$1])),
                child: Padding(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 6,
                    vertical: 4,
                  ),
                  child: Text(
                    items[i].$2,
                    style: TextStyle(
                      fontSize: 13.5,
                      fontWeight: i == items.length - 1
                          ? FontWeight.w700
                          : FontWeight.w500,
                      color: i == items.length - 1 ? c.textStrong : c.accent,
                    ),
                  ),
                ),
              ),
            ],
          ],
        );
      },
    );
  }
}
