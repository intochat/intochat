import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.TreeView.ITreeView`.
class TreeViewWidget extends StatelessWidget {
  const TreeViewWidget({
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
      kind: 'ui.tree-view',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final selectedId = state['selectedId'] as String?;
        final expanded = state['expandedIds'] is List
            ? {for (final id in state['expandedIds'] as List) '$id'}
            : const <String>{};
        final nodes = state['nodes'] is List
            ? [
                for (final n in state['nodes'] as List)
                  if (n is Map) n,
              ]
            : const <Map>[];

        List<Widget> buildLevel(String? parentId, int depth) {
          final result = <Widget>[];
          for (final n in nodes) {
            final nParent = n['parentId'] as String?;
            if (nParent != parentId) continue;
            final id = '${n['id'] ?? ''}';
            final label = '${n['label'] ?? ''}';
            final isFolder = n['isFolder'] == true;
            final isExp = expanded.contains(id);
            final isSel = id == selectedId;

            result.add(
              Padding(
                padding: EdgeInsets.only(left: depth * 18.0),
                child: Material(
                  color: isSel
                      ? c.accent.withValues(alpha: 0.12)
                      : Colors.transparent,
                  borderRadius: BorderRadius.circular(IcRadius.xs),
                  child: InkWell(
                    borderRadius: BorderRadius.circular(IcRadius.xs),
                    onTap: () => unawaited(invoke('Select', [id])),
                    child: Padding(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 8,
                        vertical: 6,
                      ),
                      child: Row(
                        children: [
                          if (isFolder)
                            GestureDetector(
                              onTap: () =>
                                  unawaited(invoke('ToggleExpand', [id])),
                              child: Icon(
                                isExp
                                    ? LucideIcons.chevronDown
                                    : LucideIcons.chevronRight,
                                size: 15,
                                color: c.textMuted,
                              ),
                            )
                          else
                            const SizedBox(width: 15),
                          const SizedBox(width: 6),
                          Icon(
                            isFolder ? LucideIcons.folder : LucideIcons.file,
                            size: 15,
                            color: isSel ? c.accent : c.textMuted,
                          ),
                          const SizedBox(width: 8),
                          Expanded(
                            child: Text(
                              label,
                              style: TextStyle(
                                fontSize: 13,
                                fontWeight: isSel
                                    ? FontWeight.w600
                                    : FontWeight.w400,
                                color: isSel ? c.accent : c.textStrong,
                              ),
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            );
            if (isFolder && isExp) {
              result.addAll(buildLevel(id, depth + 1));
            }
          }
          return result;
        }

        final itemsColumn = Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: buildLevel(null, 0),
        );

        return LayoutBuilder(
          builder: (context, constraints) {
            final stretch =
                constraints.hasBoundedHeight &&
                constraints.maxHeight.isFinite &&
                constraints.maxHeight >= 100;
            return Container(
              padding: const EdgeInsets.all(8),
              decoration: BoxDecoration(
                color: c.surfaceCard,
                borderRadius: BorderRadius.circular(IcRadius.card),
                border: Border.all(color: c.borderHairline),
              ),
              child: stretch
                  ? SingleChildScrollView(primary: false, child: itemsColumn)
                  : itemsColumn,
            );
          },
        );
      },
    );
  }
}
