import 'package:flutter/material.dart';

import '../../components/kit/ui_kit_tabs.dart';

/// Tabs value node: button children drawn as the kit tab strip. The active
/// tab is the one child with style 0 (Primary); selecting it dispatches
/// nothing, selecting another dispatches that child's action.
final class ViewTabs extends StatelessWidget {
  const ViewTabs({super.key, required this.node, required this.action});
  final Map node;
  final VoidCallback? Function(Map node) action;

  @override
  Widget build(BuildContext context) {
    final children = [
      for (final child in node['children'] as List? ?? const [])
        if (child is Map) child,
    ];
    final active = children.indexWhere((child) => child['style'] == 0);
    final index = active < 0 ? 0 : active;
    return UiKitTabs(
      labels: [for (final child in children) child['text'] as String? ?? ''],
      index: index,
      onChanged: (i) {
        if (i != index) action(children[i])?.call();
      },
    );
  }
}
