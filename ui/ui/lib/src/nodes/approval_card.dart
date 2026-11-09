import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../design/cards/approval_card.dart';
import 'node_common.dart';

/// An approval resolves only on the OS confirmation surface; in an app view it
/// is read-only and offers no buttons.
Widget renderApprovalCard(ApprovalCardNode node, NodeContext ctx) {
  final resolve = node.resolve;
  final canResolve = ctx.confirmation && resolve != null && ctx.interactive;
  return ApprovalCard(
    key: ValueKey('approval:${node.id}'),
    source: cardSource(node.systemName),
    readOnly: !canResolve,
    data: ApprovalData(
      title: node.title,
      summary: node.summary,
      rows: [
        for (final row in node.rows)
          ApprovalRow(
            label: row.label,
            oldValue: row.oldValue,
            newValue: row.newValue,
            amount: row.amount,
            warn: row.warn,
          ),
      ],
      valueLabel: node.valueLabel,
      systemName: node.systemName,
    ),
    state: _state(node.status),
    onApprove: canResolve
        ? () => ctx.resolve(resolve.planId, approve: true)
        : null,
    onReject: canResolve
        ? () => ctx.resolve(resolve.planId, approve: false)
        : null,
  );
}

ApprovalState _state(String status) => switch (status.toLowerCase()) {
  'approved' => ApprovalState.approved,
  'rejected' || 'denied' => ApprovalState.rejected,
  'error' => ApprovalState.error,
  _ => ApprovalState.pending,
};
