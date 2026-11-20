import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Table.ITable`.
class TableWidget extends StatefulWidget {
  const TableWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  State<TableWidget> createState() => _TableWidgetState();
}

class _TableWidgetState extends State<TableWidget> {
  final _filterController = TextEditingController();
  Timer? _debounce;

  @override
  void dispose() {
    _debounce?.cancel();
    _filterController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.table',
      neuronKey: widget.neuronKey,
      callNeuron: widget.callNeuron,
      watch: widget.watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = '${state['title'] ?? 'Table'}';
        final sortBy = state['sortBy'] as String?;
        final descending = state['descending'] == true;
        final page = (state['page'] as num?)?.toInt() ?? 1;
        final pageSize = (state['pageSize'] as num?)?.toInt() ?? 10;
        final total = (state['total'] as num?)?.toInt() ?? 0;
        final selectedRow = (state['selectedRowIndex'] as num?)?.toInt();
        final error = state['error'] as String?;

        final rawCols = state['columns'] is List
            ? state['columns'] as List
            : const <dynamic>[];
        final columns = <(String id, String label)>[
          for (final col in rawCols)
            if (col is Map) ('${col['id'] ?? ''}', '${col['label'] ?? ''}'),
        ];
        final rawRows = state['rows'] is List
            ? state['rows'] as List
            : const <dynamic>[];
        final rows = <List<String>>[
          for (final r in rawRows)
            if (r is List) [for (final cell in r) '$cell'],
        ];

        final totalPages = total <= 0 ? 1 : ((total - 1) ~/ pageSize) + 1;

        final tableSection = columns.isEmpty
            ? const SizedBox.shrink()
            : SingleChildScrollView(
                primary: false,
                scrollDirection: Axis.horizontal,
                child: DataTable(
                  headingRowHeight: 38,
                  dataRowMinHeight: 36,
                  dataRowMaxHeight: 40,
                  columns: [
                    for (final (id, label) in columns)
                      DataColumn(
                        label: InkWell(
                          onTap: () => unawaited(invoke('Sort', [id])),
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              Text(
                                label,
                                style: TextStyle(
                                  fontSize: 12,
                                  fontWeight: FontWeight.w600,
                                  color: sortBy == id ? c.accent : c.textMuted,
                                ),
                              ),
                              if (sortBy == id) ...[
                                const SizedBox(width: 4),
                                Icon(
                                  descending
                                      ? LucideIcons.arrowDown
                                      : LucideIcons.arrowUp,
                                  size: 13,
                                  color: c.accent,
                                ),
                              ],
                            ],
                          ),
                        ),
                      ),
                  ],
                  rows: [
                    for (var i = 0; i < rows.length; i++)
                      DataRow(
                        selected: selectedRow == i,
                        onSelectChanged: (_) => unawaited(
                          invoke('SelectRow', [selectedRow == i ? null : i]),
                        ),
                        cells: [
                          for (var col = 0; col < columns.length; col++)
                            DataCell(
                              Text(
                                col < rows[i].length ? rows[i][col] : '',
                                style: TextStyle(
                                  fontSize: 12.5,
                                  color: c.textBody,
                                  fontFeatures: const [
                                    FontFeature.tabularFigures(),
                                  ],
                                ),
                              ),
                            ),
                        ],
                      ),
                  ],
                ),
              );

        return LayoutBuilder(
          builder: (context, constraints) {
            final stretch =
                constraints.hasBoundedHeight &&
                constraints.maxHeight.isFinite &&
                constraints.maxHeight >= 180;
            return Container(
              decoration: BoxDecoration(
                color: c.surfaceCard,
                borderRadius: BorderRadius.circular(IcRadius.card),
                border: Border.all(color: c.borderHairline),
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                mainAxisSize: stretch ? MainAxisSize.max : MainAxisSize.min,
                children: [
                  Padding(
                    padding: const EdgeInsets.fromLTRB(14, 12, 14, 10),
                    child: Row(
                      children: [
                        Expanded(
                          child: Text(
                            title,
                            style: TextStyle(
                              fontSize: 14,
                              fontWeight: FontWeight.w600,
                              color: c.textStrong,
                            ),
                          ),
                        ),
                        SizedBox(
                          width: 210,
                          height: 32,
                          child: TextField(
                            controller: _filterController,
                            style: TextStyle(
                              fontSize: 12.5,
                              color: c.textStrong,
                            ),
                            decoration: InputDecoration(
                              hintText: 'Filter rows…',
                              hintStyle: TextStyle(
                                fontSize: 12.5,
                                color: c.textMuted,
                              ),
                              prefixIcon: Icon(
                                LucideIcons.search,
                                size: 14,
                                color: c.textMuted,
                              ),
                              isDense: true,
                              contentPadding: const EdgeInsets.symmetric(
                                horizontal: 10,
                                vertical: 6,
                              ),
                              border: OutlineInputBorder(
                                borderRadius: BorderRadius.circular(8),
                                borderSide: BorderSide(color: c.borderHairline),
                              ),
                            ),
                            onChanged: (text) {
                              _debounce?.cancel();
                              _debounce = Timer(
                                const Duration(milliseconds: 220),
                                () => unawaited(invoke('Filter', [text])),
                              );
                            },
                          ),
                        ),
                        const SizedBox(width: 8),
                        IconButton(
                          tooltip: 'Refresh',
                          icon: Icon(
                            LucideIcons.refreshCw,
                            size: 15,
                            color: c.textMuted,
                          ),
                          onPressed: () => unawaited(invoke('Refresh')),
                        ),
                      ],
                    ),
                  ),
                  if (error != null && error.isNotEmpty)
                    Padding(
                      padding: const EdgeInsets.fromLTRB(14, 0, 14, 10),
                      child: Text(
                        error,
                        style: TextStyle(fontSize: 12, color: c.error),
                      ),
                    ),
                  Divider(height: 1, color: c.borderHairline),
                  if (stretch)
                    Expanded(
                      child: SingleChildScrollView(
                        primary: false,
                        child: tableSection,
                      ),
                    )
                  else if (columns.isNotEmpty)
                    tableSection,
                  Divider(height: 1, color: c.borderHairline),
                  Padding(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 14,
                      vertical: 8,
                    ),
                    child: Row(
                      children: [
                        Text(
                          '$total rows · Page $page of $totalPages',
                          style: TextStyle(
                            fontSize: 12,
                            color: c.textMuted,
                            fontFeatures: const [FontFeature.tabularFigures()],
                          ),
                        ),
                        const Spacer(),
                        TextButton(
                          onPressed: page > 1
                              ? () => unawaited(invoke('Page', [page - 1]))
                              : null,
                          child: const Text('Previous'),
                        ),
                        TextButton(
                          onPressed: page < totalPages
                              ? () => unawaited(invoke('Page', [page + 1]))
                              : null,
                          child: const Text('Next'),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            );
          },
        );
      },
    );
  }
}
