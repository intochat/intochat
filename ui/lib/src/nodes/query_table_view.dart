import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../generated/node_commands.dart';
import 'error_line.dart';
import 'node_context.dart';

/// What a query table neuron draws: the query and the current page's rows
/// from the neuron's view; the title and column titles from the slot's props.
/// A header tap sends `Sort`, the filter field `Filter`, the pager `Page`, all
/// to the neuron itself (`.`); the parent checks the column and page. A source
/// error draws the table's error line instead of rows.
Widget renderQueryTableView(QueryTableViewNode node, NodeContext ctx) {
  final props = QueryTableProps.fromJson(ctx.slotProps);
  final live = props.isEnabled && ctx.interactive;
  final titles = {for (final c in props.columns) c.id: c.title};
  final pages = node.total == 0
      ? 1
      : (node.total + node.pageSize - 1) ~/ node.pageSize;
  final error = node.error;
  return Semantics(
    container: true,
    explicitChildNodes: true,
    label: controlNameOf(props.automationName, props.title, node.key),
    enabled: props.isEnabled,
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: [
        if (props.title.isNotEmpty)
          Text(
            props.title,
            style: const TextStyle(fontWeight: FontWeight.w600),
          ),
        TextFormField(
          key: ValueKey('${node.key}/filter'),
          initialValue: node.filter,
          enabled: live,
          decoration: const InputDecoration(hintText: 'Filter'),
          onFieldSubmitted: (text) => actQueryTableFilter(ctx, '.', text: text),
        ),
        if (error != null)
          ErrorLineView(
            node: ErrorLineNode(what: error, detail: null, action: null),
            ctx: ctx,
          )
        else ...[
          Row(
            children: [
              for (final id in node.columnIds)
                Expanded(
                  child: TextButton(
                    key: ValueKey('${node.key}/sort/$id'),
                    onPressed: live
                        ? () => actQueryTableSort(ctx, '.', column: id)
                        : null,
                    child: Text(
                      '${titles[id] ?? id}'
                      '${node.sortBy == id ? (node.descending ? ' ↓' : ' ↑') : ''}',
                    ),
                  ),
                ),
            ],
          ),
          for (final row in node.rows)
            Row(
              children: [for (final cell in row) Expanded(child: Text(cell))],
            ),
          if (node.loaded && node.rows.isEmpty) const Text('No rows'),
        ],
        Row(
          mainAxisAlignment: MainAxisAlignment.end,
          children: [
            IconButton(
              key: ValueKey('${node.key}/previous'),
              icon: const Icon(Icons.chevron_left),
              onPressed: live && node.page > 1
                  ? () => actQueryTablePage(ctx, '.', number: node.page - 1)
                  : null,
            ),
            Text('${node.page} / $pages'),
            IconButton(
              key: ValueKey('${node.key}/next'),
              icon: const Icon(Icons.chevron_right),
              onPressed: live && node.page < pages
                  ? () => actQueryTablePage(ctx, '.', number: node.page + 1)
                  : null,
            ),
          ],
        ),
      ],
    ),
  );
}
