import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/kit/ui_kit_surface.dart';
import '../design/cards/approval_card.dart';
import 'node_common.dart';

/// An approval resolves only on the OS confirmation surface; in an app view it
/// is read-only and offers no buttons.
Widget renderApprovalCard(ApprovalCardNode node, NodeContext ctx) {
  final resolve = node.resolve;
  if (ctx.confirmation && resolve != null) {
    return ApprovalCard(
      key: ValueKey('approval:${node.id}'),
      source: cardSource(node.systemName),
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
      onApprove: ctx.interactive
          ? () => ctx.resolve(resolve.planId, approve: true)
          : null,
      onReject: ctx.interactive
          ? () => ctx.resolve(resolve.planId, approve: false)
          : null,
    );
  }
  return Builder(
    builder: (context) {
      final text = Theme.of(context).textTheme;
      return UiKitSurface(
        key: ValueKey('approval:${node.id}'),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: 6,
          children: [
            Text(node.title, style: text.titleMedium),
            if (node.summary.isNotEmpty) Text(node.summary),
            for (final row in node.rows)
              Text('${row.label}: ${row.oldValue} -> ${row.newValue}'),
            Text(node.status, style: text.bodySmall),
          ],
        ),
      );
    },
  );
}

ApprovalState _state(String status) => switch (status.toLowerCase()) {
  'approved' => ApprovalState.approved,
  'rejected' || 'denied' => ApprovalState.rejected,
  'error' => ApprovalState.error,
  _ => ApprovalState.pending,
};
