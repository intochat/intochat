import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/kit/ui_kit_tabs.dart';
import '../generated/node_commands.dart';
import 'node_context.dart';

/// What a tab strip neuron draws: the selected id from the neuron's view; the
/// tabs, enabled and accessible name from the slot's props. A pick sends
/// `Select` to the neuron itself (`.`); the parent checks the id against the
/// props and the neuron owns the selection. A selected id naming no tab
/// highlights none. Disabled props send nothing.
Widget renderTabStripView(TabStripViewNode node, NodeContext ctx) {
  final props = TabStripProps.fromJson(ctx.slotProps);
  final enabled = props.isEnabled && ctx.interactive;
  final index = props.items.indexWhere((item) => item.id == node.selectedId);
  return Semantics(
    label: controlNameOf(props.automationName, '', node.key),
    enabled: enabled,
    child: UiKitTabs(
      labels: [for (final item in props.items) item.label],
      // A selected id that names no tab of the props selects none.
      index: index,
      disabled: enabled
          ? const {}
          : {for (var i = 0; i < props.items.length; i++) i},
      onChanged: (i) {
        if (enabled && i != index) {
          actTabStripSelect(ctx, '.', id: props.items[i].id);
        }
      },
    ),
  );
}
