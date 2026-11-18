import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/kit/ui_kit_select.dart';
import '../generated/node_commands.dart';
import 'node_context.dart';

/// What a combo box neuron draws: the selected id from the neuron's view; the
/// header, options, enabled and accessible name from the slot's props. A pick
/// sends `Select` to the neuron itself (`.`); the parent checks the id against
/// the props and the neuron owns the selection. Disabled props send nothing.
Widget renderComboBoxView(ComboBoxViewNode node, NodeContext ctx) {
  final props = ComboBoxProps.fromJson(ctx.slotProps);
  final enabled = props.isEnabled && ctx.interactive;
  final selected = props.options.any((option) => option.id == node.selectedId)
      ? node.selectedId
      : null;
  return Semantics(
    label: controlNameOf(props.automationName, props.header, node.key),
    enabled: enabled,
    child: UiKitSelect<String>(
      label: props.header.isEmpty ? null : props.header,
      value: selected,
      items: [
        for (final option in props.options)
          UiKitSelectItem(option.id, option.label),
      ],
      onChanged: enabled
          ? (id) {
              if (id != selected) actComboBoxSelect(ctx, '.', id: id);
            }
          : null,
    ),
  );
}
