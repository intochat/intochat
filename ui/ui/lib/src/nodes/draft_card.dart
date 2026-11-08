import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../design/cards/draft_card.dart';
import 'node_common.dart';

Widget renderDraftCard(DraftCardNode node, NodeContext ctx) => DraftCard(
  key: ValueKey('draft:${node.id}'),
  source: cardSource(node.channel),
  data: DraftData(
    channel: switch (node.channel.toLowerCase()) {
      'slack' => DraftChannel.slack,
      'sms' => DraftChannel.sms,
      _ => DraftChannel.email,
    },
    to: node.to,
    body: node.body,
    from: node.from,
    subject: node.subject,
  ),
  state: switch (node.status.toLowerCase()) {
    'sending' => DraftState.sending,
    'sent' => DraftState.sent,
    'scheduled' => DraftState.scheduled,
    'discarded' => DraftState.discarded,
    'error' => DraftState.error,
    _ => DraftState.pending,
  },
  onSend: pressOf(node.send, ctx),
  onDiscard: pressOf(node.discard, ctx),
);
