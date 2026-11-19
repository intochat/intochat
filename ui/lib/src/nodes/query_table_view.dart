import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../generated/node_commands.dart';
import 'error_line.dart';
import 'node_context.dart';

/// What a query table neuron draws: the query and the current page's rows
/// from the neuron's view; the title and column titles from the slot's props.
/// A header tap sends `Sort`, the filter field `Filter`, the pager `Page`, all
/// to the neuron itself (`.`); the parent checks the column and page. A source
/// error draws the table's error line instead of rows. The neuron keeps no
/// rows across a reactivation, so a view that is not loaded shows a "Not
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
    _asked = true;
    final page = node.page < 1 ? 1 : node.page;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) actQueryTablePage(widget.ctx, '.', number: page);
    });
  }

  @override
  Widget build(BuildContext context) => _draw(widget.node, widget.ctx);
}

Widget _draw(QueryTableViewNode node, NodeContext ctx) {
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
          if (!node.loaded)
            Text('Not loaded', key: ValueKey('${node.key}/notLoaded'))
          else if (node.rows.isEmpty)
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
