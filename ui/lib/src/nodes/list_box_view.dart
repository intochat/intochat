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
        for (final item in props.items)
          ListTile(
            key: ValueKey('${node.key}/${item.id}'),
            dense: true,
            enabled: props.isEnabled,
            selected:
                props.selectionMode == ListSelectionMode.single &&
                item.id == node.selectedId,
            title: Text(item.title),
            subtitle: item.subtitle == null ? null : Text(item.subtitle!),
            onTap: selectable && item.id != node.selectedId
                ? () => actListBoxSelect(ctx, '.', id: item.id)
                : null,
          ),
      ],
    ),
  );
}
