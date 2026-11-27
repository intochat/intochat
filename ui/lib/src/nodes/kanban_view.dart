import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../primitives/icon_button.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_tokens.dart';
import '../generated/node_commands.dart';
import '../theme/ui_icon.dart';
import 'node_context.dart';

/// What a kanban neuron draws: the selected card from the neuron's view; the
/// title, columns and cards from the child's props, each card in its column in
/// props order. The selection is the props' when the parent owns it, else the
/// neuron's. A tap on a card sends `Select`; a card's arrows, or a drop of a
/// dragged card on another column, send `MoveCard` to the end of that column,
/// both to the neuron itself (`.`). The board never moves a card itself: the
/// parent checks the move and moves the card in the props it renders next, so
/// a refused move leaves the board as it was. Disabled props send nothing.
Widget renderKanbanView(KanbanViewNode node, NodeContext ctx) {
  final props = KanbanProps.fromJson(ctx.childProps);
  final selectedId = props.selectedCardId ?? node.selectedCardId;
  final enabled = props.isEnabled && ctx.interactive;
  final columns = props.columns;

  List<KanbanCardItem> cardsOf(String columnId) => [
    for (final card in props.cards)
      if (card.columnId == columnId) card,
  ];

  // Moves a card to the end of another column; a card already there stays.
  void Function()? moveTo(KanbanCardItem card, KanbanColumnItem? column) =>
      !enabled || column == null || column.id == card.columnId
      ? null
      : () => actKanbanMoveCard(
          ctx,
          '.',
          cardId: card.id,
          columnId: column.id,
          index: cardsOf(column.id).length,
        );

  return Semantics(
    container: true,
    explicitChildNodes: true,
    label: controlNameOf(props.automationName, props.title, node.key),
    enabled: enabled,
    child: Builder(
      builder: (context) {
        final c = UiColors.of(context);
        return Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(UiTokens.of(context).card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Row(
                children: [
                  Icon(LucideIcons.folderKanban, size: 15, color: c.accent),
                  const SizedBox(width: 6),
                  Text(
                    props.title,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w600,
                      color: c.textStrong,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 10),
              SingleChildScrollView(
                scrollDirection: Axis.horizontal,
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    for (final (index, column) in columns.indexed) ...[
                      if (index > 0) const SizedBox(width: 10),
                      _Column(
                        key: ValueKey('${node.key}/column:${column.id}'),
                        column: column,
                        cards: cardsOf(column.id),
                        colors: c,
                        enabled: enabled,
                        accept: (cardId) => props.cards
                            .where((card) => card.id == cardId)
                            .map((card) => moveTo(card, column))
                            .firstOrNull,
                        card: (card) => _Card(
                          key: ValueKey('${node.key}/${card.id}'),
                          card: card,
                          isSelected: card.id == selectedId,
                          colors: c,
                          enabled: enabled,
                          onSelect: enabled && card.id != selectedId
                              ? () => actKanbanSelect(ctx, '.', id: card.id)
                              : null,
                          onLeft: moveTo(
                            card,
                            index > 0 ? columns[index - 1] : null,
                          ),
                          onRight: moveTo(
                            card,
                            index + 1 < columns.length
                                ? columns[index + 1]
                                : null,
                          ),
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            ],
          ),
        );
      },
    ),
  );
}

class _Column extends StatelessWidget {
  const _Column({
    super.key,
    required this.column,
    required this.cards,
    required this.colors,
    required this.enabled,
    required this.accept,
    required this.card,
  });

  final KanbanColumnItem column;
  final List<KanbanCardItem> cards;
  final UiColors colors;
  final bool enabled;
  // The move a dropped card makes into this column, or null when it makes none.
  final void Function()? Function(String cardId) accept;
  final Widget Function(KanbanCardItem card) card;

  @override
  Widget build(BuildContext context) {
    final limit = column.wipLimit;
    final count = limit == null ? '${cards.length}' : '${cards.length}/$limit';
    final icon = column.icon == null ? null : uiIcon(column.icon!);
    return DragTarget<String>(
      onWillAcceptWithDetails: (details) =>
          enabled && accept(details.data) != null,
      onAcceptWithDetails: (details) => accept(details.data)?.call(),
      builder: (context, candidates, rejected) {
        final hovering = candidates.isNotEmpty;
        // A browser finds a column by its id: `flt-semantics-identifier="column:<id>"`.
        return Semantics(
          identifier: 'column:${column.id}',
          child: Container(
            width: 220,
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(
              color: hovering ? colors.accentTint : colors.surfaceSunk,
              borderRadius: BorderRadius.circular(UiTokens.of(context).sm),
              border: Border.all(
                color: hovering ? colors.accent : colors.borderHairline,
              ),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              mainAxisSize: MainAxisSize.min,
              children: [
                Row(
                  children: [
                    if (icon != null) ...[
                      Icon(icon, size: 13, color: colors.textMuted),
                      const SizedBox(width: 6),
                    ],
                    Expanded(
                      child: Text(
                        column.title,
                        style: TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w600,
                          color: colors.textStrong,
                        ),
                      ),
                    ),
                    Text(
                      count,
                      style: TextStyle(
                        fontSize: 10.5,
                        fontWeight: FontWeight.w600,
                        color: colors.textMuted,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                if (cards.isEmpty)
                  Padding(
                    padding: const EdgeInsets.symmetric(vertical: 12),
                    child: Text(
                      'No cards',
                      textAlign: TextAlign.center,
                      style: TextStyle(fontSize: 11, color: colors.textSubtle),
                    ),
                  ),
                for (final (index, item) in cards.indexed) ...[
                  if (index > 0) const SizedBox(height: 6),
                  card(item),
                ],
              ],
            ),
          ),
        );
      },
    );
  }
}

class _Card extends StatelessWidget {
  const _Card({
    super.key,
    required this.card,
    required this.isSelected,
    required this.colors,
    required this.enabled,
    required this.onSelect,
    required this.onLeft,
    required this.onRight,
  });

  final KanbanCardItem card;
  final bool isSelected;
  final UiColors colors;
  final bool enabled;
  final VoidCallback? onSelect;
  final VoidCallback? onLeft;
  final VoidCallback? onRight;

  @override
  Widget build(BuildContext context) {
    final subtitle = card.subtitle ?? '';
    final tag = card.tag ?? '';
    final assignee = card.assignee ?? '';
    // A browser finds a card by its id: `flt-semantics-identifier="card:<id>"`.
    final tile = Semantics(
      identifier: 'card:${card.id}',
      selected: isSelected,
      child: Material(
        color: colors.surfaceCard,
        borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
        child: InkWell(
          borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
          onTap: onSelect,
          child: Container(
            padding: const EdgeInsets.all(8),
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
              border: Border.all(
                color: isSelected ? colors.accent : colors.borderHairline,
              ),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        card.title,
                        style: TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w600,
                          color: colors.textStrong,
                        ),
                      ),
                    ),
                    if (onLeft != null)
                      _Arrow(
                        key: ValueKey('left:${card.id}'),
                        icon: LucideIcons.arrowLeft,
                        tooltip: 'Move left',
                        colors: colors,
                        onPressed: onLeft!,
                      ),
                    if (onRight != null)
                      _Arrow(
                        key: ValueKey('right:${card.id}'),
                        icon: LucideIcons.arrowRight,
                        tooltip: 'Move right',
                        colors: colors,
                        onPressed: onRight!,
                      ),
                  ],
                ),
                if (subtitle.isNotEmpty) ...[
                  const SizedBox(height: 2),
                  Text(
                    subtitle,
                    style: TextStyle(fontSize: 11, color: colors.textMuted),
                  ),
                ],
                if (tag.isNotEmpty || assignee.isNotEmpty) ...[
                  const SizedBox(height: 6),
                  Row(
                    children: [
                      if (tag.isNotEmpty)
                        Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 6,
                            vertical: 1,
                          ),
                          decoration: BoxDecoration(
                            color: colors.accentTint,
                            borderRadius: BorderRadius.circular(4),
                          ),
                          child: Text(
                            tag,
                            style: TextStyle(
                              fontSize: 10,
                              fontWeight: FontWeight.w600,
                              color: colors.accent,
                            ),
                          ),
                        ),
                      const Spacer(),
                      if (assignee.isNotEmpty)
                        Text(
                          assignee,
                          style: TextStyle(
                            fontSize: 10.5,
                            color: colors.textMuted,
                          ),
                        ),
                    ],
                  ),
                ],
              ],
            ),
          ),
        ),
      ),
    );
    if (!enabled) return tile;
    return LongPressDraggable<String>(
      data: card.id,
      feedback: Material(
        elevation: 6,
        borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
        child: SizedBox(width: 200, child: tile),
      ),
      childWhenDragging: Opacity(opacity: 0.4, child: tile),
      child: tile,
    );
  }
}

class _Arrow extends StatelessWidget {
  const _Arrow({
    super.key,
    required this.icon,
    required this.tooltip,
    required this.colors,
    required this.onPressed,
  });

  final IconData icon;
  final String tooltip;
  final UiColors colors;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) => UiMixIconButton(
    tooltip: tooltip,
    size: 20,
    icon: icon,
    iconSize: 12,
    iconColor: colors.textMuted,
    onPressed: onPressed,
  );
}
