import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../core/inline_table_query.dart';
import '../design/cards/chart_card.dart';
import '../design/cards/table_card.dart';
import '../generated/node_commands.dart';
import '../primitives/button.dart';
import '../primitives/icon_button.dart';
import '../theme/ui_theme.dart';
import '../theme/wire_node_values.dart';
import 'feedback_nodes.dart';
import 'node_common.dart';
import 'node_context.dart';

// --- src/nodes/chart.dart ---

Widget renderChart(ChartNode node, NodeContext ctx) {
  final select = node.select;
  return ChartCard(
    source: cardSource(node.from),
    kind: parseChartKind(node.chartKind),
    data: ChartData(
      title: node.title,
      points: [
        for (final point in node.points) ChartPoint(point.label, point.value),
      ],
      from: node.from,
      prefix: node.prefix,
      compare: node.comparePoints,
      seriesName: node.seriesName,
      compareName: node.compareName,
      sourceNeuronUri: node.sourceNeuronUri,
    ),
    state: node.points.isEmpty ? ChartState.empty : ChartState.ready,
    onSelectPoint: ctx.interactive && select != null
        ? (index) {
            if (index >= 0 && index < node.points.length) {
              sendPick(select, ctx, node.points[index].label);
            }
          }
        : null,
  );
}

// --- src/nodes/data_table.dart ---
Widget renderDataTable(DataTableNode node, NodeContext ctx) {
  final select = node.select;
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
    onSelectRow: ctx.interactive && select != null
        ? (rowIndex) {
            if (rowIndex >= 0 && rowIndex < node.rows.length) {
              final row = node.rows[rowIndex];
              final firstCell = row.isNotEmpty ? row.first : '$rowIndex';
              sendPick(select, ctx, firstCell);
            }
          }
        : null,
  );
}

// --- src/nodes/live_chart_view.dart ---
Widget renderLiveChartView(LiveChartViewNode node, NodeContext ctx) {
  final props = LiveChartProps.fromJson(ctx.childProps);
  if (!liveChartKinds.contains(props.kind)) {
    return Semantics(
      label: props.automationName ?? props.title,
      child: Text(
        'Unknown chart kind "${props.kind}" (${liveChartKinds.join(', ')})',
      ),
    );
  }
  return Semantics(
    container: true,
    explicitChildNodes: true,
    label:
        props.automationName ?? (props.title.isEmpty ? node.key : props.title),
    child: renderChart(
      ChartNode(
        chartKind: props.kind,
        title: props.title,
        points: node.points,
        comparePoints: null,
        seriesName: '',
        compareName: '',
        sourceNeuronUri: null,
        from: 'DigitalBrain',
        prefix: '',
        select: null,
      ),
      ctx,
    ),
  );
}

// --- src/nodes/query_table_view.dart ---
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
                  child: UiMixButton.preset(
                    key: ValueKey('${node.key}/sort/$id'),
                    label:
                        '${titles[id] ?? id}'
                        '${node.sortBy == id ? (node.descending ? ' ↓' : ' ↑') : ''}',
                    style: UiMixButtonStyle.ghost,
                    compact: true,
                    onPressed: live
                        ? () => actQueryTableSort(ctx, '.', column: id)
                        : null,
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
            UiMixIconButton(
              key: ValueKey('${node.key}/previous'),
              icon: LucideIcons.chevronLeft,
              tooltip: 'Previous page',
              onPressed: live && node.page > 1
                  ? () => actQueryTablePage(ctx, '.', number: node.page - 1)
                  : null,
            ),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 6),
              child: Text(
                '${node.page} / $pages',
                style: UiType.textStyle(13, tnum: true),
              ),
            ),
            UiMixIconButton(
              key: ValueKey('${node.key}/next'),
              icon: LucideIcons.chevronRight,
              tooltip: 'Next page',
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
