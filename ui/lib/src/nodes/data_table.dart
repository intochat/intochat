import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart'
    hide TableColumn;

import 'node_context.dart';
import '../design/cards/table_card.dart';
import 'node_common.dart';

Widget renderDataTable(DataTableNode node, NodeContext ctx) => TablePreviewCard(
  source: cardSource(node.from),
  data: TableData(
    title: node.title,
    columns: [for (final column in node.columns) TableColumn(column)],
    rows: node.rows,
    from: node.from,
    sourceNeuronUri: node.sourceNeuronUri,
  ),
  state: node.rows.isEmpty ? TableState.empty : TableState.ready,
);
