import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/kit/ui_kit_surface.dart';
import 'node_common.dart';

Widget renderCard(CardNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final text = Theme.of(context).textTheme;
    return UiKitSurface(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 8,
        children: [
          if (node.title.isNotEmpty) Text(node.title, style: text.titleMedium),
          if (node.body.isNotEmpty) Text(node.body, style: text.bodyMedium),
          ...renderAll(node.children, ctx),
        ],
      ),
    );
  },
);
