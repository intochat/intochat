import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

/// Reactive WinUI 3 / DevTools-style property & JSON tree inspector for `IntoChat.OS.Modules.UI.PropertyGrid.IPropertyGrid`.
class PropertyGridNeuronWidget extends StatefulWidget {
  const PropertyGridNeuronWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  State<PropertyGridNeuronWidget> createState() =>
      _PropertyGridNeuronWidgetState();
}

class _PropertyGridNeuronWidgetState extends State<PropertyGridNeuronWidget> {
  final TextEditingController _filterController = TextEditingController();

  @override
  void dispose() {
    _filterController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.property-grid',
      neuronKey: widget.neuronKey,
      callNeuron: widget.callNeuron,
      watch: widget.watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = (state['title'] as String?) ?? 'Properties';
        final readOnly = state['readOnly'] != false;
        final selectedPath = state['selectedPath'] as String?;
        final filter = (state['filter'] as String?) ?? '';
        final expanded = state['expandedPaths'] is List
            ? {for (final p in state['expandedPaths'] as List) '$p'}
            : const <String>{};
        final rawEntries = (state['entries'] as List?) ?? const [];
        final entries = <Map<String, dynamic>>[
          for (final item in rawEntries)
            if (item is Map) Map<String, dynamic>.from(item),
        ];

        if (_filterController.text != filter &&
            !FocusScope.of(context).hasFocus) {
          _filterController.text = filter;
        }

        final lowerFilter = filter.trim().toLowerCase();
        bool isVisible(Map<String, dynamic> entry) {
          if (lowerFilter.isNotEmpty) {
            final name = '${entry['name'] ?? ''}'.toLowerCase();
            final val = '${entry['value'] ?? ''}'.toLowerCase();
            final path = '${entry['path'] ?? ''}'.toLowerCase();
            return name.contains(lowerFilter) ||
                val.contains(lowerFilter) ||
                path.contains(lowerFilter);
          }
          var parent = entry['parentPath'] as String?;
          while (parent != null && parent.isNotEmpty) {
            if (!expanded.contains(parent)) return false;
            final parentEntry = entries
                .where((e) => e['path'] == parent)
                .firstOrNull;
            parent = parentEntry?['parentPath'] as String?;
          }
          return true;
        }

        final visibleEntries = entries.where(isVisible).toList();

        return Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            children: [
              Row(
                children: [
                  Icon(LucideIcons.braces, size: 15, color: c.accent),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      title,
                      style: TextStyle(
                        fontSize: 12.5,
                        fontWeight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                  ),
                  Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 6,
                      vertical: 2,
                    ),
                    decoration: BoxDecoration(
                      color: c.surfaceSunk,
                      borderRadius: BorderRadius.circular(4),
                    ),
                    child: Text(
                      readOnly ? 'INSPECT' : 'EDITABLE',
                      style: TextStyle(
                        fontSize: 10,
                        fontWeight: FontWeight.w600,
                        color: c.textMuted,
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              SizedBox(
                height: 32,
                child: TextField(
                  controller: _filterController,
                  style: TextStyle(fontSize: 12, color: c.textStrong),
                  decoration: InputDecoration(
                    isDense: true,
                    hintText: 'Filter properties…',
                    hintStyle: TextStyle(fontSize: 12, color: c.textMuted),
                    prefixIcon: Icon(
                      LucideIcons.search,
                      size: 14,
                      color: c.textMuted,
                    ),
                    filled: true,
                    fillColor: c.surfaceSunk,
                    contentPadding: const EdgeInsets.symmetric(
                      horizontal: 8,
                      vertical: 6,
                    ),
                    border: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(IcRadius.xs),
                      borderSide: BorderSide(color: c.borderHairline),
                    ),
                    enabledBorder: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(IcRadius.xs),
                      borderSide: BorderSide(color: c.borderHairline),
                    ),
                  ),
                  onSubmitted: (q) => unawaited(invoke('SetFilter', [q])),
                  onChanged: (q) => unawaited(invoke('SetFilter', [q])),
                ),
              ),
              const SizedBox(height: 8),
              ConstrainedBox(
                constraints: const BoxConstraints(maxHeight: 280),
                child: visibleEntries.isEmpty
                    ? Padding(
                        padding: const EdgeInsets.symmetric(vertical: 16),
                        child: Text(
                          'No matching properties.',
                          textAlign: TextAlign.center,
                          style: TextStyle(fontSize: 12, color: c.textMuted),
                        ),
                      )
                    : SingleChildScrollView(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            for (final entry in visibleEntries)
                              _PropertyRow(
                                entry: entry,
                                isSelected: entry['path'] == selectedPath,
                                isExpanded: expanded.contains(entry['path']),
                                indent: lowerFilter.isEmpty
                                    ? ((entry['depth'] as num?)?.toInt() ?? 0) *
                                          14.0
                                    : 0.0,
                                readOnly: readOnly,
                                colors: c,
                                onSelect: () => unawaited(
                                  invoke('Select', ['${entry['path'] ?? ''}']),
                                ),
                                onToggleExpand: () => unawaited(
                                  invoke('ToggleExpand', [
                                    '${entry['path'] ?? ''}',
                                  ]),
                                ),
                                onEditValue: (nextVal) => unawaited(
                                  invoke('SetValue', [
                                    '${entry['path'] ?? ''}',
                                    nextVal,
                                  ]),
                                ),
                              ),
                          ],
                        ),
                      ),
              ),
            ],
          ),
        );
      },
    );
  }
}

class _PropertyRow extends StatelessWidget {
  const _PropertyRow({
    required this.entry,
    required this.isSelected,
    required this.isExpanded,
    required this.indent,
    required this.readOnly,
    required this.colors,
    required this.onSelect,
    required this.onToggleExpand,
    required this.onEditValue,
  });

  final Map<String, dynamic> entry;
  final bool isSelected;
  final bool isExpanded;
  final double indent;
  final bool readOnly;
  final IcColors colors;
  final VoidCallback onSelect;
  final VoidCallback onToggleExpand;
  final ValueChanged<String> onEditValue;

  @override
  Widget build(BuildContext context) {
    final path = '${entry['path'] ?? ''}';
    final name = '${entry['name'] ?? path}';
    final value = '${entry['value'] ?? ''}';
    final kind = '${entry['valueKind'] ?? 'string'}';
    final hasChildren = entry['hasChildren'] == true;

    final valueColor = switch (kind) {
      'number' => colors.working,
      'boolean' => colors.accent,
      'string' => colors.done,
      'null' => colors.textSubtle,
      _ => colors.textMuted,
    };

    return Semantics(
      identifier: 'property-row:$path',
      selected: isSelected,
      child: Material(
        color: isSelected ? colors.accentTint : Colors.transparent,
        borderRadius: BorderRadius.circular(IcRadius.xs),
        child: InkWell(
          borderRadius: BorderRadius.circular(IcRadius.xs),
          onTap: hasChildren ? onToggleExpand : onSelect,
          child: Padding(
            padding: EdgeInsets.only(
              left: 6 + indent,
              right: 6,
              top: 5,
              bottom: 5,
            ),
            child: Row(
              children: [
                if (hasChildren)
                  GestureDetector(
                    onTap: onToggleExpand,
                    child: Icon(
                      isExpanded
                          ? LucideIcons.chevronDown
                          : LucideIcons.chevronRight,
                      size: 14,
                      color: colors.textMuted,
                    ),
                  )
                else
                  const SizedBox(width: 14),
                const SizedBox(width: 6),
                Text(
                  name,
                  style: TextStyle(
                    fontFamily: 'JetBrains Mono',
                    fontSize: 11.5,
                    fontWeight: FontWeight.w600,
                    color: colors.textStrong,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    kind == 'string' ? '"$value"' : value,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    textAlign: TextAlign.right,
                    style: TextStyle(
                      fontFamily: 'JetBrains Mono',
                      fontSize: 11.5,
                      color: valueColor,
                    ),
                  ),
                ),
                if (!readOnly && !hasChildren) ...[
                  const SizedBox(width: 6),
                  IconButton(
                    tooltip: 'Edit $name',
                    padding: EdgeInsets.zero,
                    constraints: const BoxConstraints(
                      minWidth: 22,
                      minHeight: 22,
                    ),
                    icon: Icon(
                      LucideIcons.pencil,
                      size: 12,
                      color: colors.textMuted,
                    ),
                    onPressed: () async {
                      final controller = TextEditingController(text: value);
                      final updated = await showDialog<String>(
                        context: context,
                        builder: (ctx) => AlertDialog(
                          title: Text('Edit $name'),
                          content: TextField(controller: controller),
                          actions: [
                            TextButton(
                              onPressed: () => Navigator.of(ctx).pop(),
                              child: const Text('Cancel'),
                            ),
                            FilledButton(
                              onPressed: () =>
                                  Navigator.of(ctx).pop(controller.text),
                              child: const Text('Apply'),
                            ),
                          ],
                        ),
                      );
                      controller.dispose();
                      if (updated != null) {
                        onEditValue(updated);
                      }
                    },
                  ),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}
