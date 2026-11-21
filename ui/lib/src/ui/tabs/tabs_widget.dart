import 'dart:async';

import 'package:flutter/material.dart';

import '../../primitives/tabs.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Tabs.ITabs`.
class TabsWidget extends StatelessWidget {
  const TabsWidget({
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
      kind: 'ui.tabs',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final rawItems = state['items'] is List
            ? state['items'] as List
            : const <dynamic>[];
        final ids = <String>[];
        final labels = <String>[];
        for (final item in rawItems) {
          if (item is Map) {
            ids.add('${item['id'] ?? ''}');
            labels.add('${item['label'] ?? ''}');
          }
        }
        if (labels.isEmpty) return const SizedBox.shrink();

        final selectedId = state['selectedId'] as String?;
        final isEnabled = state['isEnabled'] != false;
        final index = selectedId == null ? 0 : ids.indexOf(selectedId);

        return UiMixTabs(
          labels: labels,
          index: index < 0 ? 0 : index,
          disabled: isEnabled
              ? const {}
              : {for (var i = 0; i < labels.length; i++) i},
          onChanged: (i) {
            if (isEnabled && i >= 0 && i < ids.length && i != index) {
              unawaited(invoke('Select', [ids[i]]));
            }
          },
        );
      },
    );
  }
}
