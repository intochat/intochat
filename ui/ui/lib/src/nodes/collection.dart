import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';

Widget renderCollection(CollectionNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final theme = Theme.of(context);
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final entry in node.entries)
          Padding(
            key: ValueKey(entry.id),
            padding: const EdgeInsets.symmetric(vertical: 6),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(entry.title, style: theme.textTheme.bodyMedium),
                      if (entry.subtitle case final subtitle?
                          when subtitle.isNotEmpty)
                        Text(subtitle, style: theme.textTheme.bodySmall),
                    ],
                  ),
                ),
                if (entry.trailing case final trailing?)
                  Text(
                    trailing,
                    style: theme.textTheme.bodySmall?.copyWith(
                      fontFeatures: const [FontFeature.tabularFigures()],
                    ),
                  ),
              ],
            ),
          ),
      ],
    );
  },
);
