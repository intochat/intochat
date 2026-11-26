import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../theme/ui_tokens.dart';
import 'node_context.dart';

/// Threshold at or above which [renderCollection] switches to a virtualized
/// [ListView.builder] so 1,000+ item lists build lazily at 60 fps (`UK-15`).
const int kVirtualizedCollectionThreshold = 32;

Widget renderCollection(CollectionNode node, NodeContext ctx) {
  if (node.entries.isEmpty) {
    return Builder(
      builder: (context) {
        final c = IcTheme.of(context);
        return Padding(
          padding: const EdgeInsets.symmetric(vertical: 12),
          child: Text(
            'Nothing here yet.',
            style: IcType.text(13.5, color: c.textMuted),
          ),
        );
      },
    );
  }

  if (node.entries.length < kVirtualizedCollectionThreshold) {
    return Builder(
      builder: (context) {
        final c = IcTheme.of(context);
        return Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            for (var i = 0; i < node.entries.length; i++)
              _CollectionRow(
                entry: node.entries[i],
                colors: c,
                showDivider: i > 0,
              ),
          ],
        );
      },
    );
  }

  return LayoutBuilder(
    builder: (context, constraints) {
      final c = IcTheme.of(context);
      final list = ListView.builder(
        key: const ValueKey('virtualized_collection_list'),
        primary: false,
        itemCount: node.entries.length,
        itemBuilder: (context, index) => _CollectionRow(
          entry: node.entries[index],
          colors: c,
          showDivider: index > 0,
        ),
      );
      if (constraints.hasBoundedHeight) {
        return list;
      }
      return ConstrainedBox(
        constraints: const BoxConstraints(maxHeight: 480),
        child: list,
      );
    },
  );
}

class _CollectionRow extends StatelessWidget {
  const _CollectionRow({
    required this.entry,
    required this.colors,
    required this.showDivider,
  });

  final Entry entry;
  final IcColors colors;
  final bool showDivider;

  @override
  Widget build(BuildContext context) {
    final subtitle = entry.subtitle;
    final trailing = entry.trailing;
    return DecoratedBox(
      key: ValueKey(entry.id),
      decoration: BoxDecoration(
        border: showDivider
            ? Border(top: BorderSide(color: colors.borderHairline))
            : null,
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 8, horizontal: 2),
        child: Row(
          children: [
            Expanded(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    entry.title,
                    style: IcType.text(
                      13.5,
                      weight: FontWeight.w500,
                      color: colors.textStrong,
                    ),
                  ),
                  if (subtitle != null && subtitle.isNotEmpty) ...[
                    const SizedBox(height: 2),
                    Text(
                      subtitle,
                      style: IcType.text(12.5, color: colors.textMuted),
                    ),
                  ],
                ],
              ),
            ),
            if (trailing != null && trailing.isNotEmpty) ...[
              const SizedBox(width: 12),
              Text(
                trailing,
                style: IcType.text(12.5, color: colors.textMuted, tnum: true),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
