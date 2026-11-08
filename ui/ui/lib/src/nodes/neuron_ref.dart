import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';

Widget renderNeuronRef(NeuronRefNode node, NodeContext ctx) => SelectableText(
  node.neuronUri,
  style: const TextStyle(fontFamily: 'monospace'),
);
