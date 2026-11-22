import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../../theme/ui_icon.dart';
import 'neuron_widget.dart';

/// Reactive WinUI 3-style workflow stage presenter for `IntoChat.OS.Modules.UI.Stepper.IStepper`.
class StepperNeuronWidget extends StatelessWidget {
  const StepperNeuronWidget({
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
      kind: 'ui.stepper',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = (state['title'] as String?) ?? '';
        final activeId = state['activeStepId'] as String?;
        final allowSelect = state['allowInteractiveSelection'] != false;
        final rawSteps = (state['steps'] as List?) ?? const [];
        final steps = <Map<String, dynamic>>[
          for (final item in rawSteps)
            if (item is Map) Map<String, dynamic>.from(item),
        ];

        final allDone =
            steps.isNotEmpty &&
            steps.every((s) => (s['status'] as String?) == 'done');

        return Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              if (title.isNotEmpty || steps.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.only(bottom: 10),
                  child: Row(
                    children: [
                      if (title.isNotEmpty)
                        Expanded(
                          child: Text(
                            title,
                            style: TextStyle(
                              fontSize: 12.5,
                              fontWeight: FontWeight.w600,
                              color: c.textStrong,
                            ),
                          ),
                        )
                      else
                        const Spacer(),
                      if (steps.isNotEmpty && !allDone)
                        Semantics(
                          identifier: 'stepper-advance:$neuronKey',
                          button: true,
                          child: TextButton.icon(
                            onPressed: () =>
                                unawaited(invoke('Advance', const [])),
                            icon: Icon(
                              LucideIcons.arrowRight,
                              size: 13,
                              color: c.accent,
                            ),
                            label: Text(
                              'Advance',
                              style: TextStyle(
                                fontSize: 11.5,
                                fontWeight: FontWeight.w600,
                                color: c.accent,
                              ),
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
              SingleChildScrollView(
                scrollDirection: Axis.horizontal,
                child: Row(
                  children: [
                    for (var i = 0; i < steps.length; i++) ...[
                      _StepNode(
                        index: i,
                        step: steps[i],
                        isActive: '${steps[i]['id'] ?? ''}' == activeId,
                        allowSelect: allowSelect,
                        colors: c,
                        onTap: () {
                          final id = '${steps[i]['id'] ?? ''}';
                          if (allowSelect && id.isNotEmpty) {
                            unawaited(invoke('Select', [id]));
                          }
                        },
                      ),
                      if (i < steps.length - 1)
                        Container(
                          width: 24,
                          height: 2,
                          margin: const EdgeInsets.symmetric(horizontal: 6),
                          color: (steps[i]['status'] as String?) == 'done'
                              ? c.done
                              : c.borderHairline,
                        ),
                    ],
                  ],
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}

class _StepNode extends StatelessWidget {
  const _StepNode({
    required this.index,
    required this.step,
    required this.isActive,
    required this.allowSelect,
    required this.colors,
    required this.onTap,
  });

  final int index;
  final Map<String, dynamic> step;
  final bool isActive;
  final bool allowSelect;
  final IcColors colors;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final id = '${step['id'] ?? ''}';
    final label = '${step['title'] ?? id}';
    final subtitle = step['subtitle'] as String?;
    final iconSlug = step['icon'] as String?;
    final status = (step['status'] as String?) ?? 'pending';

    final (
      Color badgeBg,
      Color badgeFg,
      IconData? statusIcon,
    ) = switch (status) {
      'done' => (colors.done, colors.onAccent, LucideIcons.check),
      'active' => (colors.accent, colors.onAccent, null),
      'error' => (colors.error, colors.onAccent, LucideIcons.triangleAlert),
      'skipped' => (colors.surfaceSunk, colors.textMuted, LucideIcons.minus),
      _ => (
        isActive ? colors.accent : colors.surfaceSunk,
        isActive ? colors.onAccent : colors.textMuted,
        null,
      ),
    };

    final resolvedIcon = iconSlug != null ? uiIcon(iconSlug) : statusIcon;

    return Semantics(
      identifier: 'stepper-step:$id',
      button: allowSelect,
      selected: isActive,
      child: Material(
        color: isActive ? colors.accentTint : Colors.transparent,
        borderRadius: BorderRadius.circular(IcRadius.sm),
        child: InkWell(
          borderRadius: BorderRadius.circular(IcRadius.sm),
          onTap: allowSelect ? onTap : null,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 24,
                  height: 24,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: badgeBg,
                    shape: BoxShape.circle,
                    border: Border.all(
                      color: isActive ? colors.accent : colors.borderHairline,
                    ),
                  ),
                  child: resolvedIcon != null
                      ? Icon(resolvedIcon, size: 13, color: badgeFg)
                      : Text(
                          '${index + 1}',
                          style: TextStyle(
                            fontSize: 11,
                            fontWeight: FontWeight.w700,
                            color: badgeFg,
                          ),
                        ),
                ),
                const SizedBox(width: 8),
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      label,
                      style: TextStyle(
                        fontSize: 12.5,
                        fontWeight: isActive
                            ? FontWeight.w700
                            : FontWeight.w500,
                        color: isActive ? colors.textStrong : colors.textBody,
                      ),
                    ),
                    if (subtitle != null && subtitle.isNotEmpty)
                      Text(
                        subtitle,
                        style: TextStyle(fontSize: 11, color: colors.textMuted),
                      ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
