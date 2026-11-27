import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../primitives/button.dart';
import '../controls.dart';
import '../keys.dart';
import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';
import 'card_shell.dart';

enum TableState { loading, ready, empty, error }

class TableColumn {
  const TableColumn(
    this.label, {
    this.numeric = false,
    this.date = false,
    this.flex = 3,
  });

  final String label;

  /// Right aligned, tabular figures. Sorts by the number in the text.
  final bool numeric;

  /// Day and month like "12 Aug". Sorts by date.
  final bool date;
  final int flex;
}

const _months = [
  'jan',
  'feb',
  'mar',
  'apr',
  'may',
  'jun',
  'jul',
  'aug',
  'sep',
  'oct',
  'nov',
  'dec',
];

/// Minimum row count before the quick client filter bar is shown automatically.
const int kQuickFilterRowThreshold = 8;

/// The value a cell sorts by: a number for numeric and date columns, else the
/// lowercased text.
Comparable<Object> _sortValue(String text, TableColumn col) {
  if (col.numeric) {
    return double.tryParse(text.replaceAll(RegExp(r'[^0-9.\-]'), '')) ?? 0;
  }
  if (col.date) {
    final m = RegExp(r'(\d+)\s+([A-Za-z]{3})').firstMatch(text);
    if (m == null) return 0;
    return _months.indexOf(m.group(2)!.toLowerCase()) * 40 +
        int.parse(m.group(1)!);
  }
  return text.toLowerCase();
}

class TableData {
  const TableData({
    required this.title,
    required this.columns,
    required this.rows,
    required this.from,
    this.emptyText = 'Nothing matches.',
    this.errorText = 'Something stopped the lookup. Nothing changed.',
    this.sourceNeuronUri,
  });

  final String title;
  final List<TableColumn> columns;
  final List<List<String>> rows;

  /// Where the rows come from, e.g. "Salesforce".
  final String from;
  final String emptyText;
  final String errorText;
  final String? sourceNeuronUri;
}

/// Canonical alias for [TablePreviewCard].
typedef TableCard = TablePreviewCard;

/// Table preview: rows Ino looked up, a few at a time. Click a column head to
/// sort every row in the card (numbers and dates biggest first, text A to Z;
/// again reverses, a third time goes back to Ino's order). Tables with >= 8
/// rows expose an inline quick filter input (`UK-11`).
class TablePreviewCard extends StatefulWidget {
  const TablePreviewCard({
    super.key,
    required this.source,
    required this.data,
    required this.state,
    this.onOpen,
    this.onDownload,
    this.onRetry,
    this.onSort,
    this.onFilter,
    this.onCopyNeuronUri,
    this.onSelectRow,
    this.visibleRows = 6,
    this.width = 560,
  });

  final CardSource source;
  final TableData data;
  final TableState state;
  final VoidCallback? onOpen;
  final VoidCallback? onDownload;
  final VoidCallback? onRetry;
  final void Function(int? columnIndex, bool descending)? onSort;
  final ValueChanged<String>? onFilter;
  final ValueChanged<String>? onCopyNeuronUri;
  final ValueChanged<int>? onSelectRow;
  final int visibleRows;
  final double width;

  @override
  State<TablePreviewCard> createState() => _TablePreviewCardState();
}

class _TablePreviewCardState extends State<TablePreviewCard> {
  bool _all = false;
  int? _sortCol;
  bool _desc = false;
  int? _hoverRow;
  final TextEditingController _filterController = TextEditingController();
  final FocusNode _filterFocus = FocusNode();

  @override
  void initState() {
    super.initState();
    _filterFocus.addListener(_onFilterFocus);
  }

  void _onFilterFocus() {
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    _filterFocus.removeListener(_onFilterFocus);
    _filterFocus.dispose();
    _filterController.dispose();
    super.dispose();
  }

  void _sortBy(int i) {
    final col = widget.data.columns[i];
    final firstDesc = col.numeric || col.date;
    setState(() {
      if (_sortCol != i) {
        _sortCol = i;
        _desc = firstDesc;
      } else if (_desc == firstDesc) {
        _desc = !_desc;
      } else {
        _sortCol = null;
      }
    });
    widget.onSort?.call(_sortCol, _desc);
  }

  void _updateFilter(String value) {
    setState(() {});
    widget.onFilter?.call(value);
  }

  void _clearFilter() {
    _filterController.clear();
    _updateFilter('');
    _filterFocus.requestFocus();
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final d = widget.data;
    final totalCount = d.rows.length;
    final showQuickFilter =
        widget.state == TableState.ready &&
        totalCount >= kQuickFilterRowThreshold;
    final query = _filterController.text.trim().toLowerCase();

    final indexedRows = <(int, List<String>)>[
      for (final (origIdx, r) in d.rows.indexed)
        if (query.isEmpty ||
            r.any((cell) => cell.toLowerCase().contains(query)))
          (origIdx, r),
    ];

    final sc = _sortCol;
    if (sc != null && sc < d.columns.length) {
      final col = d.columns[sc];
      indexedRows.sort((a, b) {
        final av = sc < a.$2.length ? a.$2[sc] : '';
        final bv = sc < b.$2.length ? b.$2[sc] : '';
        final x = _sortValue(av, col).compareTo(_sortValue(bv, col));
        return _desc ? -x : x;
      });
    }
    final filteredCount = indexedRows.length;
    final shown = _all || query.isNotEmpty
        ? indexedRows
        : indexedRows.take(widget.visibleRows).toList();

    Widget cell(String text, TableColumn col, {bool head = false}) {
      final hasDigits = col.numeric || col.date || RegExp(r'\d').hasMatch(text);
      return Expanded(
        flex: col.flex,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 6),
          child: Text(
            text,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            textAlign: col.numeric ? TextAlign.right : TextAlign.left,
            style: head
                ? UiType.textStyle(
                    11.5,
                    weight: FontWeight.w600,
                    color: c.textMuted,
                  )
                : UiType.textStyle(13, color: c.textStrong, tnum: hasDigits),
          ),
        ),
      );
    }

    Widget head(int i, TableColumn col) {
      final on = _sortCol == i;
      final label = Text(
        col.label,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: UiType.textStyle(
          11.5,
          weight: on ? FontWeight.w700 : FontWeight.w600,
          color: on ? c.textStrong : c.textMuted,
        ),
      );
      return Expanded(
        flex: col.flex,
        child: Semantics(
          button: true,
          label: 'Sort by ${col.label}',
          child: Stop(
            onActivate: () => _sortBy(i),
            builder: (context, v) => GestureDetector(
              key: ValueKey('table_sort_header_$i'),
              behavior: HitTestBehavior.opaque,
              onTap: () => _sortBy(i),
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: SelectionRing(
                  on: v.selected,
                  radius: UiTokens.of(context).xs,
                  child: Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 6),
                    child: Row(
                      mainAxisAlignment: col.numeric
                          ? MainAxisAlignment.end
                          : MainAxisAlignment.start,
                      children: [
                        Flexible(child: label),
                        if (on) ...[
                          const SizedBox(width: 3),
                          Icon(
                            _desc ? LucideIcons.arrowDown : LucideIcons.arrowUp,
                            size: 12,
                            color: c.textStrong,
                          ),
                        ],
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      );
    }

    final Widget body = switch (widget.state) {
      TableState.loading => Column(
        children: [
          for (var i = 0; i < 4; i++)
            Container(
              height: 34,
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 11),
              decoration: BoxDecoration(
                border: i == 0
                    ? null
                    : Border(top: BorderSide(color: c.borderHairline)),
              ),
              child: Row(
                children: [
                  for (final w in const [0.38, 0.22, 0.14])
                    Expanded(
                      child: FractionallySizedBox(
                        alignment: Alignment.centerLeft,
                        widthFactor: w / 0.38,
                        child: Container(
                          margin: const EdgeInsets.only(right: 16),
                          decoration: BoxDecoration(
                            color: c.surfaceSunk,
                            borderRadius: BorderRadius.circular(
                              UiTokens.of(context).xs,
                            ),
                          ),
                        ),
                      ),
                    ),
                ],
              ),
            ),
        ],
      ),
      TableState.empty => Padding(
        padding: const EdgeInsets.symmetric(vertical: 18, horizontal: 12),
        child: Row(
          children: [
            Icon(LucideIcons.searchX, size: 16, color: c.textMuted),
            const SizedBox(width: 10),
            Expanded(
              child: Text(
                d.emptyText,
                style: UiType.textStyle(14, color: c.textBody),
              ),
            ),
          ],
        ),
      ),
      TableState.error => Padding(
        padding: const EdgeInsets.symmetric(vertical: 14, horizontal: 12),
        child: Row(
          children: [
            Icon(LucideIcons.triangleAlert, size: 16, color: c.error),
            const SizedBox(width: 10),
            Expanded(
              child: Text(
                d.errorText,
                style: UiType.textStyle(
                  14,
                  weight: FontWeight.w500,
                  color: c.textStrong,
                ),
              ),
            ),
            UiMixButton.preset(
              label: 'Try again',
              style: UiMixButtonStyle.secondary,
              compact: true,
              onPressed: widget.onRetry,
            ),
          ],
        ),
      ),
      TableState.ready => Column(
        children: [
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 8),
            color: c.surfaceSunk,
            child: Row(
              children: [
                for (final (i, col) in d.columns.indexed) head(i, col),
              ],
            ),
          ),
          if (shown.isEmpty)
            Padding(
              key: const ValueKey('table_empty_filter_state'),
              padding: const EdgeInsets.symmetric(vertical: 18, horizontal: 12),
              child: Row(
                children: [
                  Icon(LucideIcons.searchX, size: 16, color: c.textMuted),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      'No rows match "${_filterController.text.trim()}".',
                      style: UiType.textStyle(13.5, color: c.textBody),
                    ),
                  ),
                  GestureDetector(
                    key: const ValueKey('table_clear_filter_action'),
                    onTap: _clearFilter,
                    child: MouseRegion(
                      cursor: SystemMouseCursors.click,
                      child: Text(
                        'Clear filter',
                        style: UiType.textStyle(
                          13,
                          weight: FontWeight.w600,
                          color: c.accent,
                        ),
                      ),
                    ),
                  ),
                ],
              ),
            )
          else
            for (final (origIdx, r) in shown)
              MouseRegion(
                cursor: widget.onSelectRow != null
                    ? SystemMouseCursors.click
                    : MouseCursor.defer,
                onEnter: (_) => setState(() => _hoverRow = origIdx),
                onExit: (_) => setState(() => _hoverRow = null),
                child: GestureDetector(
                  behavior: HitTestBehavior.opaque,
                  onTap: widget.onSelectRow == null
                      ? null
                      : () => widget.onSelectRow!(origIdx),
                  child: Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 6,
                      vertical: 8,
                    ),
                    decoration: BoxDecoration(
                      color: _hoverRow == origIdx ? c.hoverOverlay : null,
                      border: Border(top: BorderSide(color: c.borderHairline)),
                    ),
                    child: Row(
                      children: [
                        for (final (i, col) in d.columns.indexed)
                          cell(i < r.length ? r[i] : '', col),
                      ],
                    ),
                  ),
                ),
              ),
          if (query.isEmpty && totalCount > widget.visibleRows)
            Stop(
              onActivate: () => setState(() => _all = !_all),
              builder: (context, v) => GestureDetector(
                onTap: () => setState(() => _all = !_all),
                child: MouseRegion(
                  cursor: SystemMouseCursors.click,
                  child: Container(
                    width: double.infinity,
                    padding: const EdgeInsets.symmetric(
                      horizontal: 12,
                      vertical: 9,
                    ),
                    decoration: BoxDecoration(
                      color: v.selected ? c.accentTint : null,
                      border: Border(top: BorderSide(color: c.borderHairline)),
                    ),
                    child: Text(
                      _all ? 'Show fewer' : 'Show all $totalCount',
                      style: UiType.textStyle(
                        13,
                        weight: FontWeight.w600,
                        color: c.accent,
                        tnum: true,
                      ),
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    };

    final isGenericAppSource =
        d.from.trim().isEmpty || d.from.trim() == 'DigitalBrain';
    final countLabel = query.isNotEmpty
        ? '$filteredCount of $totalCount rows'
        : isGenericAppSource
        ? '$totalCount ${totalCount == 1 ? 'row' : 'rows'}'
        : '$totalCount ${totalCount == 1 ? 'row' : 'rows'} from ${d.from}';
    final hasFooterActions =
        d.sourceNeuronUri != null ||
        widget.onDownload != null ||
        widget.onOpen != null;

    return CardShell(
      source: widget.source,
      width: widget.width,
      showSourceHeader: !isGenericAppSource,
      status: widget.state == TableState.loading
          ? CardChip(
              label: 'Looking up',
              fg: c.working,
              bg: c.workingBg,
              icon: const WorkingSpinner(size: 12),
            )
          : null,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          if (d.title.trim().isNotEmpty || widget.state == TableState.ready)
            Row(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Expanded(
                  child: Text(
                    d.title,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: UiType.textStyle(
                      15,
                      weight: FontWeight.w600,
                      color: c.textStrong,
                    ),
                  ),
                ),
                if (widget.state == TableState.ready)
                  Text(
                    countLabel,
                    style: UiType.textStyle(
                      12.5,
                      color: c.textMuted,
                      tnum: true,
                    ),
                  ),
              ],
            ),
          if (showQuickFilter) ...[
            const SizedBox(height: 10),
            _buildFilterBox(c),
          ],
          const SizedBox(height: 10),
          ClipRRect(
            borderRadius: BorderRadius.circular(UiTokens.of(context).button),
            child: Container(
              decoration: BoxDecoration(
                borderRadius: BorderRadius.circular(
                  UiTokens.of(context).button,
                ),
                border: Border.all(color: c.borderHairline),
              ),
              child: body,
            ),
          ),
          if (widget.state == TableState.ready && hasFooterActions) ...[
            const SizedBox(height: 12),
            Wrap(
              alignment: WrapAlignment.end,
              spacing: 8,
              runSpacing: 8,
              children: [
                if (d.sourceNeuronUri != null)
                  UiMixButton.preset(
                    label: 'Copy URI',
                    style: UiMixButtonStyle.secondary,
                    iconData: LucideIcons.copy,
                    compact: true,
                    onPressed: () {
                      final uri = d.sourceNeuronUri!;
                      Clipboard.setData(ClipboardData(text: uri));
                      widget.onCopyNeuronUri?.call(uri);
                    },
                  ),
                if (widget.onDownload != null)
                  UiMixButton.preset(
                    label: 'Download CSV',
                    style: UiMixButtonStyle.secondary,
                    iconData: LucideIcons.download,
                    compact: true,
                    onPressed: widget.onDownload,
                  ),
                if (widget.onOpen != null)
                  UiMixButton.preset(
                    label: 'Open in Sheets',
                    style: UiMixButtonStyle.secondary,
                    iconData: LucideIcons.grid3x3,
                    compact: true,
                    onPressed: widget.onOpen,
                  ),
              ],
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildFilterBox(UiColors c) {
    final focused = _filterFocus.hasFocus;
    final hasText = _filterController.text.isNotEmpty;
    return Stack(
      clipBehavior: Clip.none,
      children: [
        if (focused)
          Positioned(
            left: -3,
            top: -3,
            right: -3,
            bottom: -3,
            child: IgnorePointer(
              child: DecoratedBox(
                decoration: BoxDecoration(
                  borderRadius: BorderRadius.circular(
                    UiTokens.of(context).button + 3,
                  ),
                  border: Border.all(color: c.ring, width: 3),
                ),
              ),
            ),
          ),
        Container(
          key: const ValueKey('table_quick_filter_box'),
          height: 34,
          padding: const EdgeInsets.symmetric(horizontal: 10),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(UiTokens.of(context).button),
            border: Border.all(color: focused ? c.accent : c.borderInput),
          ),
          child: Row(
            children: [
              Icon(LucideIcons.search, size: 14, color: c.textMuted),
              const SizedBox(width: 8),
              Expanded(
                child: CallbackShortcuts(
                  bindings: {
                    if (hasText)
                      const SingleActivator(LogicalKeyboardKey.escape):
                          _clearFilter,
                  },
                  child: TextField(
                    key: const ValueKey('table_quick_filter_input'),
                    controller: _filterController,
                    focusNode: _filterFocus,
                    style: UiType.textStyle(13, color: c.textStrong),
                    cursorColor: c.accent,
                    decoration: InputDecoration.collapsed(
                      hintText: 'Filter rows…',
                      hintStyle: UiType.textStyle(13, color: c.textSubtle),
                    ),
                    onChanged: _updateFilter,
                  ),
                ),
              ),
              if (hasText)
                GestureDetector(
                  key: const ValueKey('table_quick_filter_clear'),
                  behavior: HitTestBehavior.opaque,
                  onTap: _clearFilter,
                  child: MouseRegion(
                    cursor: SystemMouseCursors.click,
                    child: Padding(
                      padding: const EdgeInsets.all(4),
                      child: Icon(LucideIcons.x, size: 14, color: c.textMuted),
                    ),
                  ),
                ),
            ],
          ),
        ),
      ],
    );
  }
}
