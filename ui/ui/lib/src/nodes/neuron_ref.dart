import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';

/// A live neuron surface (a viewport, a chat, a composer) hosted in place; without a host, its address.
Widget renderNeuronRef(NeuronRefNode node, NodeContext ctx) =>
    ctx.reference?.call(node.refKind, node.neuronUri) ??
    SelectableText(
      node.neuronUri,
      style: const TextStyle(fontFamily: 'monospace'),
    );
