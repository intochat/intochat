import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../theme/ui_icon.dart';

Widget renderNavItem(NavItemNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final theme = Theme.of(context);
    return ListTile(
      key: ValueKey('nav:${node.key}'),
      dense: true,
      selected: node.selected,
      leading: Icon(uiIcon(node.icon) ?? Icons.circle_outlined, size: 16),
      title: Text(node.label),
      trailing: node.badge == null
          ? null
          : Text(node.badge!, style: theme.textTheme.labelSmall),
      onTap: ctx.interactive ? () => ctx.activate(node.key) : null,
    );
  },
);
