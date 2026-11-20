import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Expander.IExpander`.
class ExpanderWidget extends StatelessWidget {
  const ExpanderWidget({
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
      kind: 'ui.expander',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = '${state['title'] ?? 'Details'}';
        final body = '${state['body'] ?? ''}';
        final isExpanded = state['isExpanded'] == true;
        final isEnabled = state['isEnabled'] != false;

        return Container(
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            children: [
              InkWell(
                borderRadius: BorderRadius.circular(IcRadius.card),
                onTap: isEnabled ? () => unawaited(invoke('Toggle')) : null,
                child: Padding(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 14,
                    vertical: 11,
                  ),
                  child: Row(
                    children: [
                      Expanded(
                        child: Text(
                          title,
                          style: TextStyle(
                            fontSize: 13.5,
                            fontWeight: FontWeight.w600,
                            color: isEnabled ? c.textStrong : c.textMuted,
                          ),
                        ),
                      ),
                      Icon(
                        isExpanded
                            ? LucideIcons.chevronUp
                            : LucideIcons.chevronDown,
                        size: 16,
                        color: c.textMuted,
                      ),
                    ],
                  ),
                ),
              ),
              if (isExpanded && body.isNotEmpty) ...[
                Divider(height: 1, color: c.borderHairline),
                Padding(
                  padding: const EdgeInsets.all(14),
                  child: Text(
                    body,
                    style: TextStyle(
                      fontSize: 13,
                      height: 1.45,
                      color: c.textBody,
                    ),
                  ),
                ),
              ],
            ],
          ),
        );
      },
    );
  }
}
