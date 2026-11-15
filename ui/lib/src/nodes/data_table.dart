import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart'
    hide TableColumn;

import '../design/cards/table_card.dart';
import 'node_common.dart';
import 'node_context.dart';

final _numericPattern = RegExp(
  r'^\s*[$€£¥+\-−]?\s*\d[\d,.\s]*[%KMBkmb]?\s*$',
);

bool _isNumericColumn(List<List<String>> rows, int columnIndex) {
  var nonEmpty = 0;
  for (final row in rows) {
    if (columnIndex >= row.length) continue;
    final cell = row[columnIndex].trim();
    if (cell.isEmpty || cell == '—' || cell == '-') continue;
    nonEmpty++;
    if (!_numericPattern.hasMatch(cell)) return false;
  }
  return nonEmpty > 0;
}

Widget renderDataTable(DataTableNode node, NodeContext ctx) => TablePreviewCard(
  source: cardSource(node.from),
  data: TableData(
    title: node.title,
    columns: [
      for (final (i, column) in node.columns.indexed)
        TableColumn(column, numeric: _isNumericColumn(node.rows, i)),
    ],
    rows: node.rows,
    from: node.from,
    sourceNeuronUri: node.sourceNeuronUri,
  ),
  state: node.rows.isEmpty ? TableState.empty : TableState.ready,
);
