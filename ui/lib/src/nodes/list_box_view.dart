import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../generated/node_commands.dart';
import 'node_context.dart';

/// What a list box neuron draws: the selected id from the neuron's view; the
/// items, selection mode, enabled and accessible name from the slot's props.
/// A tap sends `Select` to the neuron itself (`.`); the parent checks the id
/// against the props and the neuron owns the selection. Disabled props, or a
/// selection mode of none, send nothing.
Widget renderListBoxView(ListBoxViewNode node, NodeContext ctx) {
  final props = ListBoxProps.fromJson(ctx.slotProps);
  // A controlled control draws the parent's selection; otherwise the neuron's.
  final selectedId = props.selectedId ?? node.selectedId;
  final selectable =
      props.isEnabled &&
      ctx.interactive &&
      props.selectionMode == ListSelectionMode.single;
  return Semantics(
    label: controlNameOf(props.automationName, '', node.key),
    enabled: props.isEnabled,
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: [
        for (final (index, item) in props.items.indexed) ...[
          // A group header where the group changes from the item before.
          if (item.group != null &&
              (index == 0 || props.items[index - 1].group != item.group))
            Padding(
              key: ValueKey('${node.key}/group:${item.group}'),
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
              child: Builder(
                builder: (context) => Text(
                  item.group!,
                  style: Theme.of(context).textTheme.labelSmall,
                ),
              ),
            ),
          ListTile(
            key: ValueKey('${node.key}/${item.id}'),
            dense: true,
            enabled: props.isEnabled,
            selected:
                props.selectionMode == ListSelectionMode.single &&
                item.id == selectedId,
            title: Text(item.title),
            subtitle: item.subtitle == null ? null : Text(item.subtitle!),
            onTap: selectable && item.id != selectedId
                ? () => actListBoxSelect(ctx, '.', id: item.id)
                : null,
          ),
        ],
      ],
    ),
  );
}
