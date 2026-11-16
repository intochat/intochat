import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../design/cards/draft_card.dart';
import '../theme/wire_node_values.dart';
import 'node_common.dart';

Widget renderDraftCard(DraftCardNode node, NodeContext ctx) => DraftCard(
  key: ValueKey('draft:${node.id}'),
  source: cardSource(node.channel),
  data: DraftData(
    channel: parseDraftChannel(node.channel),
    to: node.to,
    body: node.body,
    from: node.from,
    subject: node.subject,
  ),
  state: parseDraftState(node.status),
  onSend: pressOf(node.send, ctx),
  onDiscard: pressOf(node.discard, ctx),
);
