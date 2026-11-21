import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../ui/mix/ui_mix_surface.dart';
import 'node_common.dart';
import 'node_renderer.dart';

/// A card frame: an optional header, the title and body, the children and an
/// optional footer, top to bottom.
Widget renderCard(CardNode node, NodeContext ctx) => LayoutBuilder(
  builder: (context, constraints) {
    final text = Theme.of(context).textTheme;
    final singleFill =
        node.children.length == 1 && NodeRenderer.fills(node.children.single);
    final stretch =
        singleFill &&
        constraints.hasBoundedHeight &&
        constraints.maxHeight.isFinite &&
        constraints.maxHeight >= 180;
    final renderedChildren = renderAll(node.children, ctx);
    return UiMixSurface(
      child: Column(
        mainAxisSize: stretch ? MainAxisSize.max : MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 8,
        children: [
          ...renderAll([?node.header], ctx),
          if (node.title.isNotEmpty) Text(node.title, style: text.titleMedium),
          if (node.body.isNotEmpty) Text(node.body, style: text.bodyMedium),
          if (stretch)
            Expanded(child: renderedChildren.single)
          else
            ...renderedChildren,
          ...renderAll([?node.footer], ctx),
        ],
      ),
    );
  },
);
