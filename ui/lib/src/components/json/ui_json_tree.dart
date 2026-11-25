import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';

final class _Row {
  const _Row({
    required this.path,
    required this.name,
    required this.depth,
    required this.value,
    this.parent,
  });

  final String path;
  final String name;
  final int depth;
  final Object? value;
  final String? parent;

  bool get isContainer => value is Map || value is List;

  String get display => switch (value) {
    final Map<String, dynamic> map => '{${map.length}}',
    final List<dynamic> list => '[${list.length}]',
    final String text => '"$text"',
    null => 'null',
    final other => '$other',
  };
}

List<_Row> _flatten(Object? root) {
  final rows = <_Row>[];
  void visit(Object? value, String? parent, int depth) {
    void add(String name, String path, Object? child) {
      rows.add(
        _Row(
          path: path,
          name: name,
          depth: depth,
          value: child,
          parent: parent,
        ),
      );
      visit(child, path, depth + 1);
    }

    if (value is Map<String, dynamic>) {
      for (final MapEntry(:key, value: child) in value.entries) {
        add(key, parent == null ? key : '$parent.$key', child);
      }
    } else if (value is List<dynamic>) {
      for (var i = 0; i < value.length; i++) {
        add('[$i]', '${parent ?? ''}[$i]', value[i]);
      }
    }
  }

  if (root is Map || root is List) {
    visit(root, null, 0);
  } else {
    rows.add(_Row(path: r'$', name: r'$', depth: 0, value: root));
  }
  return rows;
}

/// Read-only JSON as a tree the person can expand and filter. Which members
/// are open and the filter are the viewer's own: they live here and are never
/// sent. Text that does not parse is shown as it is.
class UiJsonTree extends StatefulWidget {
  const UiJsonTree({super.key, required this.title, required this.json});

  final String title;
  final String json;

  @override
  State<UiJsonTree> createState() => _UiJsonTreeState();
}

class _UiJsonTreeState extends State<UiJsonTree> {
  final _filter = TextEditingController();
  final _expanded = <String>{};
  String? _parsedFrom;
  List<_Row>? _rows;

  @override
  void dispose() {
    _filter.dispose();
    super.dispose();
  }

  List<_Row>? _parse(String json) {
    if (_parsedFrom == json) return _rows;
    _parsedFrom = json;
    try {
      _rows = _flatten(jsonDecode(json));
    } on FormatException {
      _rows = null;
    }
    return _rows;
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final rows = _parse(widget.json);
    final query = _filter.text.trim().toLowerCase();

    final Widget body;
    if (rows == null) {
      body = Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        spacing: 6,
        children: [
          Text('Not valid JSON', style: UiType.textStyle(11, color: c.muted)),
          SelectableText(
            widget.json,
            style: UiType.tabularStyle(11.5, color: c.textBody),
          ),
        ],
      );
    } else {
      final byPath = {for (final row in rows) row.path: row};
      bool open(_Row row) {
        for (var p = row.parent; p != null; p = byPath[p]?.parent) {
          if (!_expanded.contains(p)) return false;
        }
        return true;
      }

      bool matches(_Row row) =>
          row.name.toLowerCase().contains(query) ||
          row.path.toLowerCase().contains(query) ||
          (!row.isContainer && row.display.toLowerCase().contains(query));

      final visible = [
        for (final row in rows)
          if (query.isEmpty ? open(row) : matches(row)) row,
      ];
      body = visible.isEmpty
          ? Padding(
              padding: const EdgeInsets.symmetric(vertical: 16),
              child: Text(
                'No matching properties.',
                textAlign: TextAlign.center,
                style: UiType.textStyle(12, color: c.muted),
              ),
            )
          : ConstrainedBox(
              constraints: const BoxConstraints(maxHeight: 360),
              child: SingleChildScrollView(
                primary: false,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    for (final row in visible)
                      _JsonRow(
                        row: row,
                        indent: query.isEmpty ? row.depth * 14.0 : 0,
                        expanded: _expanded.contains(row.path),
                        onToggle: row.isContainer
                            ? () => setState(() {
                                if (!_expanded.remove(row.path)) {
                                  _expanded.add(row.path);
                                }
                              })
                            : null,
                      ),
                  ],
                ),
              ),
            );
    }

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        borderRadius: BorderRadius.circular(tokens.surfaceRadius),
        border: Border.all(color: c.borderHairline),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        spacing: 8,
        children: [
          Row(
            children: [
              Icon(LucideIcons.braces, size: 15, color: c.accent),
              const SizedBox(width: 6),
              Expanded(
                child: Text(
                  widget.title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: UiType.textStyle(
                    12.5,
                    weight: FontWeight.w600,
                    color: c.textStrong,
                  ),
                ),
              ),
            ],
          ),
          if (rows != null)
            SizedBox(
              height: 32,
              child: TextField(
                key: const ValueKey('json:filter'),
                controller: _filter,
                onChanged: (_) => setState(() {}),
                style: UiType.textStyle(12, color: c.textStrong),
                decoration: InputDecoration(
                  isDense: true,
                  hintText: 'Filter properties…',
                  prefixIcon: Icon(
                    LucideIcons.search,
                    size: 14,
                    color: c.muted,
                  ),
                  contentPadding: const EdgeInsets.symmetric(
                    horizontal: 8,
                    vertical: 6,
                  ),
                ),
              ),
            ),
          body,
        ],
      ),
    );
  }
}

class _JsonRow extends StatelessWidget {
  const _JsonRow({
    required this.row,
    required this.indent,
    required this.expanded,
    required this.onToggle,
  });

  final _Row row;
  final double indent;
  final bool expanded;
  final VoidCallback? onToggle;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final valueColor = switch (row.value) {
      num() => c.working,
      bool() => c.accent,
      String() => c.done,
      null => c.subtle,
      _ => c.muted,
    };
    final content = Padding(
      padding: EdgeInsets.only(left: 6 + indent, right: 6, top: 5, bottom: 5),
      child: Row(
        children: [
          if (row.isContainer)
            Icon(
              expanded ? LucideIcons.chevronDown : LucideIcons.chevronRight,
              size: 14,
              color: c.muted,
            )
          else
            const SizedBox(width: 14),
          const SizedBox(width: 6),
          Text(
            row.name,
            style: UiType.tabularStyle(
              11.5,
              weight: FontWeight.w600,
              color: c.textStrong,
            ),
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              row.display,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              textAlign: TextAlign.right,
              style: UiType.tabularStyle(11.5, color: valueColor),
            ),
          ),
        ],
      ),
    );
    return Semantics(
      identifier: 'json-row:${row.path}',
      expanded: row.isContainer ? expanded : null,
      child: onToggle == null
          ? content
          : InkWell(
              key: ValueKey('json:${row.path}'),
              borderRadius: BorderRadius.circular(4),
              onTap: onToggle,
              child: content,
            ),
    );
  }
}
