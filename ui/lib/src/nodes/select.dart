import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../ui/mix/ui_mix_select.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

/// Renders either a crisp desktop Segmented Pill Switcher (when `label` starts
/// with `'segmented:'`) or a dropdown [UiKitSelect].
Widget renderSelect(SelectNode node, NodeContext ctx) {
  final isSegmented = node.label.startsWith('segmented:');
  if (isSegmented) {
    final caption = node.label.substring('segmented:'.length).trim();
    return _SegmentedSelectBar(node: node, ctx: ctx, caption: caption);
  }

  final selected = node.options
      .where((option) => option.id == node.selected)
      .firstOrNull;
  return UiKitSelect<String>(
    label: node.label,
    value: selected?.key,
    items: [
      for (final option in node.options)
        UiKitSelectItem(option.key, option.label, enabled: option.emit != null),
    ],
    onChanged: ctx.interactive
        ? (key) {
            if (key != selected?.key) ctx.activate(key);
          }
        : null,
  );
}

class _SegmentedSelectBar extends StatelessWidget {
  const _SegmentedSelectBar({
    required this.node,
    required this.ctx,
    required this.caption,
  });

  final SelectNode node;
  final NodeContext ctx;
  final String caption;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);

    final track = Container(
      padding: const EdgeInsets.all(3),
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        borderRadius: BorderRadius.circular(tokens.button),
        border: Border.all(color: c.borderHairline),
      ),
      child: Wrap(
        spacing: 2,
        runSpacing: 2,
        children: [
          for (final opt in node.options)
            _SegmentItem(
              key: ValueKey('segment:${opt.key}'),
              label: opt.label,
              selected: opt.id == node.selected,
              enabled: ctx.interactive && opt.emit != null,
              onTap: () {
                if (opt.id != node.selected) {
                  ctx.activate(opt.key);
                }
              },
            ),
        ],
      ),
    );

    if (caption.isEmpty) return track;

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          caption,
          style: UiType.textStyle(12, weight: FontWeight.w600, color: c.muted),
        ),
        const SizedBox(width: 8),
        Flexible(child: track),
      ],
    );
  }
}

class _SegmentItem extends StatefulWidget {
  const _SegmentItem({
    super.key,
    required this.label,
    required this.selected,
    required this.enabled,
    required this.onTap,
  });

  final String label;
  final bool selected;
  final bool enabled;
  final VoidCallback onTap;

  @override
  State<_SegmentItem> createState() => _SegmentItemState();
}

class _SegmentItemState extends State<_SegmentItem> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final selected = widget.selected;

    return MouseRegion(
      cursor: widget.enabled
          ? SystemMouseCursors.click
          : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: widget.enabled ? widget.onTap : null,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 120),
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
          decoration: BoxDecoration(
            color: selected
                ? c.surfaceCard
                : (_hover && widget.enabled
                      ? c.surfaceCard.withValues(alpha: 0.45)
                      : Colors.transparent),
            borderRadius: BorderRadius.circular(
              (tokens.button - 2).clamp(4, 16),
            ),
            border: selected ? Border.all(color: c.borderInput) : null,
          ),
          child: Text(
            widget.label,
            style: UiType.textStyle(
              12.5,
              weight: selected ? FontWeight.w600 : FontWeight.w500,
              color: selected ? c.textStrong : c.muted,
            ),
          ),
        ),
      ),
    );
  }
}
