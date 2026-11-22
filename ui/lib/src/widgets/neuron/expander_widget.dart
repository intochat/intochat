import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../../primitives/primitives.dart';
import 'neuron_widget.dart';

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
        final title = '${state['title'] ?? state['header'] ?? 'Details'}';
        final subtitle = '${state['summary'] ?? ''}';
        final body = '${state['body'] ?? ''}';
        final isExpanded = state['isExpanded'] == true;
        final isEnabled = state['isEnabled'] != false;

        return UiMixExpander(
          header: title,
          subtitle: subtitle,
          isExpanded: isExpanded,
          enabled: isEnabled,
          semanticsId: neuronKey,
          onExpandedChanged: (_) {
            if (isEnabled) unawaited(invoke('Toggle'));
          },
          body: body.isNotEmpty
              ? Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Divider(height: 1, color: c.borderHairline),
                    Padding(
                      padding: const EdgeInsets.only(top: 10),
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
                )
              : const SizedBox.shrink(),
        );
      },
    );
  }
}
