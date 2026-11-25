import '../generated/node_models.dart';

/// One drawn row of a query table: its id (null for a row read from a
/// source, which cannot be selected) and its cells in column order.
typedef ShownTableRow = ({String? id, List<String> cells});

/// The rows a query table draws from its props' inline rows: the neuron's
/// query applied in the client, because the neuron never holds the rows.
/// The filter is a case-insensitive contains on any cell, the sort orders by
/// one column (numbers as numbers, otherwise ordinal; stable), and the page
/// is the slice of `pageSize` rows. [total] is how many rows matched.
({List<ShownTableRow> rows, int total}) queryInlineRows(
  List<QueryTableRow> rows,
  List<String> columnIds, {
  required String filter,
  required String? sortBy,
  required bool descending,
  required int page,
  required int pageSize,
}) {
  final needle = filter.toLowerCase();
  final matched = [
    for (final row in rows)
      if (needle.isEmpty ||
          row.cells.any((cell) => cell.toLowerCase().contains(needle)))
        row,
  ];
  final at = sortBy == null ? -1 : columnIds.indexOf(sortBy);
  if (at >= 0) {
    String cell(QueryTableRow row) =>
        at < row.cells.length ? row.cells[at] : '';
    final order = List<int>.generate(matched.length, (i) => i);
    order.sort((i, j) {
      final a = cell(matched[i]);
      final b = cell(matched[j]);
      final x = num.tryParse(a);
      final y = num.tryParse(b);
      final by = x != null && y != null ? x.compareTo(y) : a.compareTo(b);
      final directed = descending ? -by : by;
      return directed != 0 ? directed : i.compareTo(j);
    });
    final sorted = [for (final i in order) matched[i]];
    matched
      ..clear()
      ..addAll(sorted);
  }
  final size = pageSize < 1 ? 1 : pageSize;
  final start = ((page < 1 ? 1 : page) - 1) * size;
  return (
    rows: [
      for (final row in matched.skip(start).take(size))
        (
          id: row.id,
          cells: [
            for (var i = 0; i < columnIds.length; i++)
              i < row.cells.length ? row.cells[i] : '',
          ],
        ),
    ],
    total: matched.length,
  );
}
