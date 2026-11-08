import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import 'node_common.dart';

/// The neuron topology as a list of its nodes; the 3D scene is the brain app's own surface.
Widget renderBrainScene(BrainSceneNode node, NodeContext ctx) => Wrap(
  spacing: 8,
  runSpacing: 8,
  children: [
    for (final neuron in node.nodes)
      ChoiceChip(
        key: ValueKey('neuron:${neuron.id}'),
        label: Text(neuron.label),
        selected: neuron.id == node.selectedId,
        onSelected: switch (pressOf(neuron.select, ctx)) {
          final press? => (_) => press(),
          null => null,
        },
      ),
  ],
);
