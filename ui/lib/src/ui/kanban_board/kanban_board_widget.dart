import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../../theme/ui_icon.dart';
import '../neuron_widget.dart';

/// Reactive multi-column Kanban board presenter for `IntoChat.OS.Modules.UI.KanbanBoard.IKanbanBoard`.
class KanbanBoardNeuronWidget extends StatelessWidget {
  const KanbanBoardNeuronWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.kanban',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = (state['title'] as String?) ?? 'Board';
        final selectedCardId = state['selectedCardId'] as String?;
        final rawColumns = (state['columns'] as List?) ?? const [];
        final rawCards = (state['cards'] as List?) ?? const [];

        final columns = <Map<String, dynamic>>[
          for (final item in rawColumns)
            if (item is Map) Map<String, dynamic>.from(item),
        ];
        final cards = <Map<String, dynamic>>[
          for (final item in rawCards)
            if (item is Map) Map<String, dynamic>.from(item),
        ];

        return Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.card),
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
                    title,
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
                    for (var colIdx = 0; colIdx < columns.length; colIdx++) ...[
                      if (colIdx > 0) const SizedBox(width: 10),
                      _KanbanColumnView(
                        column: columns[colIdx],
                        cards:
                            cards
                                .where(
                                  (card) =>
                                      card['columnId'] == columns[colIdx]['id'],
                                )
                                .toList()
                              ..sort(
                                (a, b) =>
                                    ((a['order'] as num?)?.toInt() ?? 0) -
                                    ((b['order'] as num?)?.toInt() ?? 0),
                              ),
                        selectedCardId: selectedCardId,
                        prevColumnId: colIdx > 0
                            ? '${columns[colIdx - 1]['id'] ?? ''}'
                            : null,
                        nextColumnId: colIdx + 1 < columns.length
                            ? '${columns[colIdx + 1]['id'] ?? ''}'
                            : null,
                        colors: c,
                        onSelectCard: (cardId) =>
                            unawaited(invoke('SelectCard', [cardId])),
                        onMoveCard: (cardId, targetColId, order) => unawaited(
                          invoke('MoveCard', [cardId, targetColId, order]),
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
    );
  }
}

class _KanbanColumnView extends StatelessWidget {
  const _KanbanColumnView({
    required this.column,
    required this.cards,
    required this.selectedCardId,
    required this.prevColumnId,
    required this.nextColumnId,
    required this.colors,
    required this.onSelectCard,
    required this.onMoveCard,
  });

  final Map<String, dynamic> column;
  final List<Map<String, dynamic>> cards;
  final String? selectedCardId;
  final String? prevColumnId;
  final String? nextColumnId;
  final IcColors colors;
  final ValueChanged<String> onSelectCard;
  final void Function(String cardId, String targetColumnId, int order)
  onMoveCard;

  @override
  Widget build(BuildContext context) {
    final colId = '${column['id'] ?? ''}';
    final colTitle = '${column['title'] ?? colId}';
    final iconSlug = column['icon'] as String?;
    final wip = (column['wipLimit'] as num?)?.toInt();
    final countText = wip != null ? '${cards.length}/$wip' : '${cards.length}';
    final canMoveSelectedHere =
        selectedCardId != null &&
        selectedCardId!.isNotEmpty &&
        cards.every((c) => '${c['id'] ?? ''}' != selectedCardId);

    return DragTarget<String>(
      onWillAcceptWithDetails: (details) => details.data.isNotEmpty,
      onAcceptWithDetails: (details) =>
          onMoveCard(details.data, colId, cards.length),
      builder: (context, candidateData, rejectedData) {
        final hovering = candidateData.isNotEmpty;
        return Semantics(
          identifier: 'kanban-col:$colId',
          child: Container(
            width: 220,
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(
              color: hovering ? colors.accentTint : colors.surfaceSunk,
              borderRadius: BorderRadius.circular(IcRadius.sm),
              border: Border.all(
                color: hovering ? colors.accent : colors.borderHairline,
              ),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              mainAxisSize: MainAxisSize.min,
              children: [
                InkWell(
                  borderRadius: BorderRadius.circular(IcRadius.xs),
                  onTap: canMoveSelectedHere
                      ? () => onMoveCard(selectedCardId!, colId, cards.length)
                      : null,
                  child: Row(
                    children: [
                      if (iconSlug != null && uiIcon(iconSlug) != null) ...[
                        Icon(
                          uiIcon(iconSlug),
                          size: 13,
                          color: colors.textMuted,
                        ),
                        const SizedBox(width: 6),
                      ],
                      Expanded(
                        child: Text(
                          colTitle,
                          style: TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.w600,
                            color: colors.textStrong,
                          ),
                        ),
                      ),
                      Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 6,
                          vertical: 1,
                        ),
                        decoration: BoxDecoration(
                          color: colors.surfaceCard,
                          borderRadius: BorderRadius.circular(99),
                        ),
                        child: Text(
                          countText,
                          style: TextStyle(
                            fontSize: 10.5,
                            fontWeight: FontWeight.w600,
                            color: colors.textMuted,
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 8),
                if (cards.isEmpty)
                  Padding(
                    padding: const EdgeInsets.symmetric(vertical: 12),
                    child: Text(
                      'Drop cards here',
                      textAlign: TextAlign.center,
                      style: TextStyle(fontSize: 11, color: colors.textSubtle),
                    ),
                  ),
                for (var i = 0; i < cards.length; i++) ...[
                  if (i > 0) const SizedBox(height: 6),
                  _KanbanCardTile(
                    card: cards[i],
                    isSelected: cards[i]['id'] == selectedCardId,
                    prevColumnId: prevColumnId,
                    nextColumnId: nextColumnId,
                    colors: colors,
                    onSelect: () => onSelectCard('${cards[i]['id'] ?? ''}'),
                    onMoveTo: (targetCol) =>
                        onMoveCard('${cards[i]['id'] ?? ''}', targetCol, 0),
                  ),
                ],
              ],
            ),
          ),
        );
      },
    );
  }
}

class _KanbanCardTile extends StatelessWidget {
  const _KanbanCardTile({
    required this.card,
    required this.isSelected,
    required this.prevColumnId,
    required this.nextColumnId,
    required this.colors,
    required this.onSelect,
    required this.onMoveTo,
  });

  final Map<String, dynamic> card;
  final bool isSelected;
  final String? prevColumnId;
  final String? nextColumnId;
  final IcColors colors;
  final VoidCallback onSelect;
  final ValueChanged<String> onMoveTo;

  @override
  Widget build(BuildContext context) {
    final id = '${card['id'] ?? ''}';
    final title = '${card['title'] ?? id}';
    final subtitle = card['subtitle'] as String?;
    final tag = card['tag'] as String?;
    final assignee = card['assignee'] as String?;

    final tile = Semantics(
      identifier: 'kanban-card:$id',
      selected: isSelected,
      child: Material(
        color: colors.surfaceCard,
        borderRadius: BorderRadius.circular(IcRadius.xs),
        child: InkWell(
          borderRadius: BorderRadius.circular(IcRadius.xs),
          onTap: onSelect,
          child: Container(
            padding: const EdgeInsets.all(8),
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(IcRadius.xs),
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
                        title,
                        style: TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w600,
                          color: colors.textStrong,
                        ),
                      ),
                    ),
                    if (prevColumnId != null)
                      IconButton(
                        tooltip: 'Move left',
                        padding: EdgeInsets.zero,
                        constraints: const BoxConstraints(
                          minWidth: 20,
                          minHeight: 20,
                        ),
                        icon: Icon(
                          LucideIcons.arrowLeft,
                          size: 12,
                          color: colors.textMuted,
                        ),
                        onPressed: () => onMoveTo(prevColumnId!),
                      ),
                    if (nextColumnId != null)
                      IconButton(
                        tooltip: 'Move right',
                        padding: EdgeInsets.zero,
                        constraints: const BoxConstraints(
                          minWidth: 20,
                          minHeight: 20,
                        ),
                        icon: Icon(
                          LucideIcons.arrowRight,
                          size: 12,
                          color: colors.textMuted,
                        ),
                        onPressed: () => onMoveTo(nextColumnId!),
                      ),
                  ],
                ),
                if (subtitle != null && subtitle.isNotEmpty) ...[
                  const SizedBox(height: 2),
                  Text(
                    subtitle,
                    style: TextStyle(fontSize: 11, color: colors.textMuted),
                  ),
                ],
                if ((tag != null && tag.isNotEmpty) ||
                    (assignee != null && assignee.isNotEmpty)) ...[
                  const SizedBox(height: 6),
                  Row(
                    children: [
                      if (tag != null && tag.isNotEmpty)
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
                      if (assignee != null && assignee.isNotEmpty)
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

    return LongPressDraggable<String>(
      data: id,
      feedback: Material(
        elevation: 6,
        borderRadius: BorderRadius.circular(IcRadius.xs),
        child: SizedBox(width: 200, child: tile),
      ),
      childWhenDragging: Opacity(opacity: 0.4, child: tile),
      child: tile,
    );
  }
}
