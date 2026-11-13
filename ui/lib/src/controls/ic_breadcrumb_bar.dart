import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/tokens.dart';

/// A single navigable segment in an [IcBreadcrumbBar] (WinUI 3 `BreadcrumbBarItem`).
@immutable
class IcBreadcrumbItem {
  const IcBreadcrumbItem({
    required this.id,
    required this.label,
    this.icon,
    this.onTap,
    this.itemKey,
  });

  final String id;
  final String label;
  final IconData? icon;
  final VoidCallback? onTap;
  final Key? itemKey;
}

/// WinUI 3 `BreadcrumbBar` navigation control for hierarchical paths (`Files`, `Creator`, neuron paths).
///
/// Features:
/// - Root/Storage segment with icon
/// - Responsive ellipsis (`•••`) overflow flyout menu when deep paths exceed available width
/// - Crisp chevron (`›`) separators
/// - Active last-segment emphasis
/// - Optional trailing `neuron://` URI pill and action buttons
class IcBreadcrumbBar extends StatelessWidget {
  const IcBreadcrumbBar({
    super.key,
    required this.items,
    this.neuronUri,
    this.neuronUriKey,
    this.trailingActions = const [],
    this.maxVisibleItems = 5,
  });

  final List<IcBreadcrumbItem> items;
  final String? neuronUri;
  final Key? neuronUriKey;
  final List<Widget> trailingActions;
  final int maxVisibleItems;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return LayoutBuilder(
      builder: (context, constraints) {
        final availableWidth = constraints.hasBoundedWidth
            ? constraints.maxWidth
            : 960.0;
        final responsiveLimit = availableWidth < 480
            ? 2
            : availableWidth < 680
            ? 3
            : availableWidth < 880
            ? 4
            : maxVisibleItems;

        final hasOverflow =
            items.length > responsiveLimit && responsiveLimit >= 2;
        final tailCount = hasOverflow
            ? (responsiveLimit - 1).clamp(1, items.length - 1)
            : items.length;
        final collapsedItems = hasOverflow
            ? items.sublist(1, items.length - tailCount)
            : const <IcBreadcrumbItem>[];
        final tailItems = hasOverflow
            ? items.sublist(items.length - tailCount)
            : items;

        return IcAcrylicBox(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          borderRadius: BorderRadius.circular(IcRadius.button),
          child: Wrap(
            spacing: 4,
            runSpacing: 6,
            crossAxisAlignment: WrapCrossAlignment.center,
            alignment: WrapAlignment.spaceBetween,
            children: [
              Wrap(
                spacing: 2,
                runSpacing: 4,
                crossAxisAlignment: WrapCrossAlignment.center,
                children: [
                  if (items.isEmpty)
                    Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(
                          LucideIcons.hardDrive,
                          size: 13,
                          color: c.textMuted,
                        ),
                        const SizedBox(width: 6),
                        Text(
                          'Storages',
                          style: IcType.text(
                            12.5,
                            weight: FontWeight.w600,
                            color: c.textBody,
                          ),
                        ),
                      ],
                    )
                  else ...[
                    if (hasOverflow) ...[
                      _BreadcrumbSegmentButton(
                        item: items.first,
                        isCurrent: false,
                        isRoot: true,
                      ),
                      _Chevron(color: c.textMuted),
                      _OverflowFlyoutButton(collapsed: collapsedItems),
                      _Chevron(color: c.textMuted),
                      for (var i = 0; i < tailItems.length; i++) ...[
                        if (i > 0) _Chevron(color: c.textMuted),
                        _BreadcrumbSegmentButton(
                          item: tailItems[i],
                          isCurrent: i == tailItems.length - 1,
                          isRoot: false,
                        ),
                      ],
                    ] else
                      for (var i = 0; i < items.length; i++) ...[
                        if (i > 0) _Chevron(color: c.textMuted),
                        _BreadcrumbSegmentButton(
                          item: items[i],
                          isCurrent: i == items.length - 1,
                          isRoot: i == 0,
                        ),
                      ],
                  ],
                ],
              ),
              if ((neuronUri != null && neuronUri!.isNotEmpty) ||
                  trailingActions.isNotEmpty)
                Wrap(
                  spacing: 6,
                  runSpacing: 4,
                  crossAxisAlignment: WrapCrossAlignment.center,
                  children: [
                    if (neuronUri != null && neuronUri!.isNotEmpty)
                      Container(
                        key: neuronUriKey,
                        padding: const EdgeInsets.symmetric(
                          horizontal: 8,
                          vertical: 3,
                        ),
                        decoration: BoxDecoration(
                          color: c.surfaceSunk,
                          borderRadius: BorderRadius.circular(IcRadius.pill),
                          border: Border.all(color: c.borderInput),
                        ),
                        child: Text(
                          neuronUri!,
                          style: IcType.code(11, color: c.textBody),
                        ),
                      ),
                    ...trailingActions,
                  ],
                ),
            ],
          ),
        );
      },
    );
  }
}

class _Chevron extends StatelessWidget {
  const _Chevron({required this.color});
  final Color color;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(horizontal: 2),
    child: Icon(LucideIcons.chevronRight, size: 13, color: color),
  );
}

class _OverflowFlyoutButton extends StatelessWidget {
  const _OverflowFlyoutButton({required this.collapsed});
  final List<IcBreadcrumbItem> collapsed;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return PopupMenuButton<int>(
      key: const ValueKey('breadcrumb-overflow-button'),
      tooltip: 'More parent folders',
      color: c.surfaceCard,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(IcRadius.button),
        side: BorderSide(color: c.borderInput),
      ),
      onSelected: (index) => collapsed[index].onTap?.call(),
      itemBuilder: (context) => [
        for (var i = 0; i < collapsed.length; i++)
          PopupMenuItem<int>(
            value: i,
            height: 34,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  collapsed[i].icon ?? LucideIcons.folder,
                  size: 13,
                  color: c.accent,
                ),
                const SizedBox(width: 8),
                Text(
                  collapsed[i].label,
                  style: IcType.text(
                    12.5,
                    weight: FontWeight.w500,
                    color: c.textStrong,
                  ),
                ),
              ],
            ),
          ),
      ],
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 4),
        decoration: BoxDecoration(
          color: c.surfaceSunk,
          borderRadius: BorderRadius.circular(6),
          border: Border.all(color: c.borderInput),
        ),
        child: Icon(LucideIcons.ellipsis, size: 13, color: c.textBody),
      ),
    );
  }
}

class _BreadcrumbSegmentButton extends StatefulWidget {
  const _BreadcrumbSegmentButton({
    required this.item,
    required this.isCurrent,
    required this.isRoot,
  });

  final IcBreadcrumbItem item;
  final bool isCurrent;
  final bool isRoot;

  @override
  State<_BreadcrumbSegmentButton> createState() =>
      _BreadcrumbSegmentButtonState();
}

class _BreadcrumbSegmentButtonState extends State<_BreadcrumbSegmentButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final icon =
        widget.item.icon ??
        (widget.isRoot ? LucideIcons.hardDrive : LucideIcons.folder);

    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      cursor: widget.item.onTap != null
          ? SystemMouseCursors.click
          : SystemMouseCursors.basic,
      child: GestureDetector(
        key: widget.item.itemKey,
        onTap: widget.item.onTap,
        child: AnimatedContainer(
          duration: IcMotion.fast,
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
          decoration: BoxDecoration(
            color: _hovered
                ? c.accentTint
                : widget.isCurrent
                ? c.surfaceCard
                : Colors.transparent,
            borderRadius: BorderRadius.circular(6),
            border: Border.all(
              color: widget.isCurrent
                  ? c.borderInput
                  : _hovered
                  ? c.accent.withValues(alpha: 0.35)
                  : Colors.transparent,
            ),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(
                icon,
                size: 13,
                color: widget.isCurrent || _hovered ? c.accent : c.textMuted,
              ),
              const SizedBox(width: 5),
              Flexible(
                child: Text(
                  widget.item.label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: IcType.text(
                    12.5,
                    weight: widget.isCurrent
                        ? FontWeight.w600
                        : FontWeight.w500,
                    color: widget.isCurrent ? c.textStrong : c.textBody,
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
