import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/tokens.dart';
import '../generated/node_commands.dart';
import 'node_context.dart';

/// What a stepper neuron draws: the active step from the neuron's view; the
/// title and steps (each with its status) from the child's props. The active
/// step is the props' when the parent owns it, else the neuron's. A tap on a
/// step that is not active sends `Select` to the neuron itself (`.`); the
/// parent checks the id against the props. Disabled props send nothing.
Widget renderStepperView(StepperViewNode node, NodeContext ctx) {
  final props = StepperProps.fromJson(ctx.childProps);
  final activeId = props.activeStepId ?? node.activeStepId;
  final enabled = props.isEnabled && ctx.interactive;
  final title = props.title ?? '';
  return Semantics(
    container: true,
    explicitChildNodes: true,
    label: controlNameOf(props.automationName, title, node.key),
    enabled: enabled,
    child: Builder(
      builder: (context) {
        final c = IcTheme.of(context);
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
              if (title.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.only(bottom: 10),
                  child: Text(
                    title,
                    style: TextStyle(
                      fontSize: 12.5,
                      fontWeight: FontWeight.w600,
                      color: c.textStrong,
                    ),
                  ),
                ),
              SingleChildScrollView(
                scrollDirection: Axis.horizontal,
                child: Row(
                  children: [
                    for (final (index, step) in props.steps.indexed) ...[
                      _Step(
                        key: ValueKey('${node.key}/${step.id}'),
                        index: index,
                        step: step,
                        isActive: step.id == activeId,
                        colors: c,
                        onTap: enabled && step.id != activeId
                            ? () => actStepperSelect(ctx, '.', id: step.id)
                            : null,
                      ),
                      if (index < props.steps.length - 1)
                        Container(
                          width: 24,
                          height: 2,
                          margin: const EdgeInsets.symmetric(horizontal: 6),
                          color: step.status == StepperStatus.done
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
    ),
  );
}

class _Step extends StatelessWidget {
  const _Step({
    super.key,
    required this.index,
    required this.step,
    required this.isActive,
    required this.colors,
    required this.onTap,
  });

  final int index;
  final StepperItem step;
  final bool isActive;
  final IcColors colors;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final (badge, onBadge, icon) = switch (step.status) {
      StepperStatus.done => (colors.done, colors.onAccent, LucideIcons.check),
      StepperStatus.active => (colors.accent, colors.onAccent, null),
      StepperStatus.error => (
        colors.error,
        colors.onAccent,
        LucideIcons.triangleAlert,
      ),
      StepperStatus.skipped => (
        colors.surfaceSunk,
        colors.textMuted,
        LucideIcons.minus,
      ),
      StepperStatus.pending => (
        isActive ? colors.accent : colors.surfaceSunk,
        isActive ? colors.onAccent : colors.textMuted,
        null,
      ),
    };
    final description = step.description ?? '';
    // A browser finds a step by its id: `flt-semantics-identifier="step:<id>"`.
    return Semantics(
      identifier: 'step:${step.id}',
      button: onTap != null,
      selected: isActive,
      child: Material(
        color: isActive ? colors.accentTint : Colors.transparent,
        borderRadius: BorderRadius.circular(IcRadius.sm),
        child: InkWell(
          borderRadius: BorderRadius.circular(IcRadius.sm),
          onTap: onTap,
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
                    color: badge,
                    shape: BoxShape.circle,
                    border: Border.all(
                      color: isActive ? colors.accent : colors.borderHairline,
                    ),
                  ),
                  child: icon != null
                      ? Icon(icon, size: 13, color: onBadge)
                      : Text(
                          '${index + 1}',
                          style: TextStyle(
                            fontSize: 11,
                            fontWeight: FontWeight.w700,
                            color: onBadge,
                          ),
                        ),
                ),
                const SizedBox(width: 8),
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      step.label,
                      style: TextStyle(
                        fontSize: 12.5,
                        fontWeight: isActive
                            ? FontWeight.w700
                            : FontWeight.w500,
                        color: isActive ? colors.textStrong : colors.textBody,
                      ),
                    ),
                    if (description.isNotEmpty)
                      Text(
                        description,
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
