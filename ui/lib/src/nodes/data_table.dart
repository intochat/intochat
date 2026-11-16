import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart'
    hide TableColumn;

import '../design/cards/table_card.dart';
import '../theme/wire_node_values.dart';
import 'node_common.dart';
import 'node_context.dart';

Widget renderDataTable(DataTableNode node, NodeContext ctx) {
  final actionKey = ctx.nodeKeys['dataTable:${node.title}'];
  final isChatCard =
      ctx.view.contains('/apps/ino') &&
      node.from.trim().isNotEmpty &&
      !isDigitalBrainSourceName(node.from);
  return TablePreviewCard(
    source: cardSource(node.from),
    width: isChatCard ? 560 : double.infinity,
    visibleRows: isChatCard ? 8 : 25,
    data: TableData(
      title: node.title,
      columns: [
        for (final (i, column) in node.columns.indexed)
          TableColumn(column, numeric: isNumericTableColumn(node.rows, i)),
      ],
      rows: node.rows,
      from: node.from,
      sourceNeuronUri: node.sourceNeuronUri,
    ),
    state: node.rows.isEmpty ? TableState.empty : TableState.ready,
    onSelectRow: ctx.interactive && actionKey != null && actionKey.isNotEmpty
        ? (rowIndex) {
            if (rowIndex >= 0 && rowIndex < node.rows.length) {
              final row = node.rows[rowIndex];
              final firstCell = row.isNotEmpty ? row.first : '$rowIndex';
              ctx.activate(actionKey, value: firstCell);
            }
          }
        : null,
  );
}
