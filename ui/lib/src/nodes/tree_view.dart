import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/tokens.dart';
import '../generated/node_commands.dart';
import '../theme/ui_icon.dart';
import 'node_context.dart';

/// What a tree neuron draws: the selected node and the expanded folders from
/// the neuron's view; the nodes from the child's props, each under its parent
/// and siblings in props order. The selection and the expanded folders are
/// each the props' when the parent owns them, else the neuron's. A tap on a
/// node sends `Select`, a tap on a folder's chevron `ExpandNode` with the
/// opposite state, both to the neuron itself (`.`); the parent checks the id
/// against the props. A node other nodes sit in is a folder; a node whose
/// parent is not drawn is not drawn either. Disabled props send nothing.
Widget renderTreeView(TreeViewNode node, NodeContext ctx) {
  final props = TreeProps.fromJson(ctx.childProps);
  final selectedId = props.selectedId ?? node.selectedId;
  final expanded = {...(props.expandedIds ?? node.expandedIds)};
  final enabled = props.isEnabled && ctx.interactive;
  final parents = {for (final item in props.nodes) ?item.parentId};

  // `drawn` guards against parent ids that form a cycle: a node is drawn once.
  List<Widget> level(
    String? parentId,
    int depth,
    IcColors c,
    Set<String> drawn,
  ) => [
    for (final item in props.nodes)
      if (item.parentId == parentId && drawn.add(item.id)) ...[
        _TreeRow(
          key: ValueKey('${node.key}/${item.id}'),
          item: item,
          depth: depth,
          isFolder: item.isFolder || parents.contains(item.id),
          isExpanded: expanded.contains(item.id),
          isSelected: item.id == selectedId,
          colors: c,
          onSelect: enabled && item.id != selectedId
              ? () => actTreeSelect(ctx, '.', id: item.id)
              : null,
          onExpand: enabled
              ? () => actTreeExpandNode(
                  ctx,
                  '.',
                  id: item.id,
                  isExpanded: !expanded.contains(item.id),
                )
              : null,
        ),
        if (expanded.contains(item.id)) ...level(item.id, depth + 1, c, drawn),
      ],
  ];

  return Semantics(
    container: true,
    explicitChildNodes: true,
    label: controlNameOf(props.automationName, '', node.key),
    enabled: enabled,
    child: Builder(
      builder: (context) {
        final c = IcTheme.of(context);
        return Container(
          padding: const EdgeInsets.all(8),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            children: level(null, 0, c, <String>{}),
          ),
        );
      },
    ),
  );
}

class _TreeRow extends StatelessWidget {
  const _TreeRow({
    super.key,
    required this.item,
    required this.depth,
    required this.isFolder,
    required this.isExpanded,
    required this.isSelected,
    required this.colors,
    required this.onSelect,
    required this.onExpand,
  });

  final TreeItem item;
  final int depth;
  final bool isFolder;
  final bool isExpanded;
  final bool isSelected;
  final IcColors colors;
  final VoidCallback? onSelect;
  final VoidCallback? onExpand;

  @override
  Widget build(BuildContext context) {
    final icon =
        (item.icon == null ? null : uiIcon(item.icon!)) ??
        (isFolder ? LucideIcons.folder : LucideIcons.file);
    // A browser finds a node by its id: `flt-semantics-identifier="node:<id>"`.
    return Semantics(
      identifier: 'node:${item.id}',
      selected: isSelected,
      expanded: isFolder ? isExpanded : null,
      child: Padding(
        padding: EdgeInsets.only(left: depth * 18.0),
        child: Material(
          color: isSelected
              ? colors.accent.withValues(alpha: 0.12)
              : Colors.transparent,
          borderRadius: BorderRadius.circular(IcRadius.xs),
          child: InkWell(
            borderRadius: BorderRadius.circular(IcRadius.xs),
            onTap: onSelect,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
              child: Row(
                children: [
                  if (isFolder)
                    InkWell(
                      key: ValueKey('chevron:${item.id}'),
                      onTap: onExpand,
                      child: Icon(
                        isExpanded
                            ? LucideIcons.chevronDown
                            : LucideIcons.chevronRight,
                        size: 15,
                        color: colors.textMuted,
                      ),
                    )
                  else
                    const SizedBox(width: 15),
                  const SizedBox(width: 6),
                  Icon(
                    icon,
                    size: 15,
                    color: isSelected ? colors.accent : colors.textMuted,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      item.label,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: isSelected
                            ? FontWeight.w600
                            : FontWeight.w400,
                        color: isSelected ? colors.accent : colors.textStrong,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
