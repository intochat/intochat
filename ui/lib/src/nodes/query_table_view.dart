import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../core/inline_table_query.dart';
import '../generated/node_commands.dart';
import 'error_line.dart';
import 'node_context.dart';

/// What a query table neuron draws: the query from the neuron's view; the
/// title and column titles from the child's props. The rows are either the
/// props' inline rows, which this view filters (a case-insensitive contains
/// on any cell), sorts (by one column, numbers as numbers) and pages by the
/// neuron's query, or the current page the neuron read from its source.
/// A header tap sends `Sort`, the filter field `Filter`, the pager `Page`, and
/// an inline row tap `SelectRow` with the row's id, all to the neuron itself
/// (`.`); the parent checks the column, page and row id. The selected row is
/// the props' when the parent owns it, else the neuron's. A source error draws
/// the table's error line instead of rows. The neuron keeps no source rows
/// across a reactivation, so a sourced view that is not loaded shows a "Not
/// loaded" line and sends `Page` for its current page once, which reads it.
Widget renderQueryTableView(QueryTableViewNode node, NodeContext ctx) =>
    _QueryTable(node: node, ctx: ctx);

class _QueryTable extends StatefulWidget {
  const _QueryTable({required this.node, required this.ctx});

  final QueryTableViewNode node;
  final NodeContext ctx;

  @override
  State<_QueryTable> createState() => _QueryTableState();
}

class _QueryTableState extends State<_QueryTable> {
  bool _asked = false;

  @override
  void initState() {
    super.initState();
    _askIfNotLoaded();
  }

  @override
  void didUpdateWidget(_QueryTable old) {
    super.didUpdateWidget(old);
    _askIfNotLoaded();
  }

  void _askIfNotLoaded() {
    final node = widget.node;
    if (_asked || node.loaded || !widget.ctx.interactive) return;
    if (QueryTableProps.fromJson(widget.ctx.childProps).rows != null) return;
    _asked = true;
    final page = node.page < 1 ? 1 : node.page;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) actQueryTablePage(widget.ctx, '.', number: page);
    });
  }

  @override
  Widget build(BuildContext context) => _draw(context, widget.node, widget.ctx);
}

/// The rows a table draws: the page the neuron read, or the props' inline
/// rows through the neuron's query.
({List<String> columnIds, List<ShownTableRow> rows, int total, bool loaded})
_shown(QueryTableViewNode node, QueryTableProps props) {
  final inline = props.rows;
  if (inline == null) {
    return (
      columnIds: node.columnIds,
      rows: [for (final cells in node.rows) (id: null, cells: cells)],
      total: node.total,
      loaded: node.loaded,
    );
  }
  final columnIds = [for (final c in props.columns) c.id];
  final shown = queryInlineRows(
    inline,
    columnIds,
    filter: node.filter,
    sortBy: node.sortBy,
    descending: node.descending,
    page: node.page,
    pageSize: node.pageSize,
  );
  return (
    columnIds: columnIds,
    rows: shown.rows,
    total: shown.total,
    loaded: true,
  );
}

Widget _draw(BuildContext context, QueryTableViewNode node, NodeContext ctx) {
  final props = QueryTableProps.fromJson(ctx.childProps);
  final live = props.isEnabled && ctx.interactive;
  final titles = {for (final c in props.columns) c.id: c.title};
  final shown = _shown(node, props);
  final selectedId = props.selectedRowId ?? node.selectedRowId;
  final pages = shown.total == 0
      ? 1
      : (shown.total + node.pageSize - 1) ~/ node.pageSize;
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
              for (final id in shown.columnIds)
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
          for (final row in shown.rows)
            if (row.id case final id?)
              Semantics(
                key: ValueKey('${node.key}/row/$id'),
                button: true,
                selected: id == selectedId,
                child: Material(
                  color: id == selectedId
                      ? Theme.of(context).colorScheme.secondaryContainer
                      : Colors.transparent,
                  child: InkWell(
                    onTap: live
                        ? () => actQueryTableSelectRow(ctx, '.', rowId: id)
                        : null,
                    child: Row(
                      children: [
                        for (final cell in row.cells)
                          Expanded(child: Text(cell)),
                      ],
                    ),
                  ),
                ),
              )
            else
              Row(
                children: [
                  for (final cell in row.cells) Expanded(child: Text(cell)),
                ],
              ),
          if (!shown.loaded)
            Text('Not loaded', key: ValueKey('${node.key}/notLoaded'))
          else if (shown.rows.isEmpty)
            const Text('No rows'),
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
