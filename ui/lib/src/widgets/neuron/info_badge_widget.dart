import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../../theme/ui_icon.dart';
import 'neuron_widget.dart';

/// Reactive WinUI 3 InfoBadge presenter for `IntoChat.OS.Modules.UI.InfoBadge.IInfoBadge`.
class InfoBadgeNeuronWidget extends StatelessWidget {
  const InfoBadgeNeuronWidget({
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
      kind: 'ui.info-badge',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        if (state['isVisible'] == false) {
          return const SizedBox.shrink();
        }
        final c = IcTheme.of(context);
        final label = (state['label'] as String?) ?? '';
        final value = (state['value'] as num?)?.toInt();
        final iconSlug = state['icon'] as String?;
        final severity = (state['severity'] as String?) ?? 'attention';
        final kind = (state['kind'] as String?) ?? 'dot';

        final (Color fill, Color fg) = switch (severity) {
          'success' => (c.done, c.onAccent),
          'caution' => (c.working, c.surfaceInk),
          'critical' => (c.error, c.onAccent),
          'informational' => (c.surfaceSunk, c.textStrong),
          _ => (c.accent, c.onAccent),
        };

        final resolvedIcon = iconSlug != null ? uiIcon(iconSlug) : null;

        final Widget badge = switch (kind) {
          'count' when value != null => Container(
            constraints: const BoxConstraints(minWidth: 20, minHeight: 20),
            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
            alignment: Alignment.center,
            decoration: BoxDecoration(
              color: fill,
              borderRadius: BorderRadius.circular(99),
            ),
            child: Text(
              value > 99 ? '99+' : '$value',
              style: TextStyle(
                fontSize: 11,
                fontWeight: FontWeight.w700,
                color: fg,
              ),
            ),
          ),
          'icon' when resolvedIcon != null => Container(
            width: 20,
            height: 20,
            alignment: Alignment.center,
            decoration: BoxDecoration(color: fill, shape: BoxShape.circle),
            child: Icon(resolvedIcon, size: 12, color: fg),
          ),
          _ => Container(
            width: 10,
            height: 10,
            decoration: BoxDecoration(color: fill, shape: BoxShape.circle),
          ),
        };

        if (label.isEmpty) {
          return badge;
        }

        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(color: c.borderHairline),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                label,
                style: TextStyle(
                  fontSize: 12.5,
                  fontWeight: FontWeight.w500,
                  color: c.textStrong,
                ),
              ),
              const SizedBox(width: 8),
              badge,
            ],
          ),
        );
      },
    );
  }
}
