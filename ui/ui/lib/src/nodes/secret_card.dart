import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../design/cards/secret_card.dart';
import 'node_common.dart';

Widget renderSecretCard(SecretCardNode node, NodeContext ctx) => SecretCard(
  source: cardSource(node.savedIn),
  data: SecretRequestData(
    name: node.name,
    why: node.why,
    savedIn: node.savedIn,
  ),
  state: switch (node.status.toLowerCase()) {
    'entering' => SecretState.entering,
    'saved' => SecretState.saved,
    'skipped' => SecretState.skipped,
    _ => SecretState.pending,
  },
  onEnter: pressOf(node.enter, ctx),
  onSkip: pressOf(node.skip, ctx),
);
