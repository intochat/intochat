import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../design/tokens.dart';
import '../../theme/ui_icon.dart';
import '../neuron_widget.dart';

class GridViewNeuronWidget extends StatefulWidget {
  const GridViewNeuronWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  State<GridViewNeuronWidget> createState() => _GridViewNeuronWidgetState();
}

class _GridViewNeuronWidgetState extends State<GridViewNeuronWidget> {
  String? _copiedSlug;

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.grid-view',
      neuronKey: widget.neuronKey,
      callNeuron: widget.callNeuron,
      watch: widget.watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = (state['title'] as String?) ?? 'Grid';
        final selectedId = state['selectedId'] as String?;
        final columns = ((state['columns'] as num?)?.toInt() ?? 3).clamp(1, 6);
        final items = ((state['items'] as List?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .toList();

        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (title.isNotEmpty || _copiedSlug != null)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Row(
                  children: [
                    if (title.isNotEmpty)
                      Expanded(
                        child: Text(
                          title,
                          style: TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w600,
                            color: c.textStrong,
                          ),
                        ),
                      )
                    else
                      const Spacer(),
                    if (_copiedSlug != null)
                      Semantics(
                        identifier: 'key:grid-copied-slug',
                        child: Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 8,
                            vertical: 2,
                          ),
                          decoration: BoxDecoration(
                            color: c.doneBg,
                            borderRadius: BorderRadius.circular(99),
                          ),
                          child: Text(
                            'Copied: $_copiedSlug',
                            style: TextStyle(
                              fontSize: 11,
                              fontWeight: FontWeight.w600,
                              color: c.done,
                            ),
                          ),
                        ),
                      ),
                  ],
                ),
              ),
            GridView.builder(
              shrinkWrap: true,
              physics: const NeverScrollableScrollPhysics(),
              gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
                crossAxisCount: columns,
                crossAxisSpacing: 8,
                mainAxisSpacing: 8,
                childAspectRatio: 1.6,
              ),
              itemCount: items.length,
              itemBuilder: (context, i) {
                final item = items[i];
                final id = (item['id'] as String?) ?? '$i';
                final itemTitle = (item['title'] as String?) ?? id;
                final subtitle = item['subtitle'] as String?;
                final iconSlug = item['icon'] as String?;
                final badge = item['badge'] as String?;
                final isSelected = id == selectedId;
                return InkWell(
                  borderRadius: BorderRadius.circular(IcRadius.sm),
                  onTap: () {
                    unawaited(Clipboard.setData(ClipboardData(text: id)));
                    setState(() => _copiedSlug = id);
                    unawaited(invoke('Select', [id]));
                  },
                  child: Container(
                    padding: const EdgeInsets.all(10),
                    decoration: BoxDecoration(
                      color: isSelected
                          ? c.accent.withValues(alpha: 0.12)
                          : c.surfaceCard,
                      borderRadius: BorderRadius.circular(IcRadius.sm),
                      border: Border.all(
                        color: isSelected ? c.accent : c.borderHairline,
                      ),
                    ),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        Row(
                          children: [
                            Icon(
                              uiIcon(iconSlug ?? 'file') ??
                                  Icons.insert_drive_file_outlined,
                              size: 18,
                              color: isSelected ? c.accent : c.textMuted,
                            ),
                            const Spacer(),
                            if (badge != null && badge.isNotEmpty)
                              Text(
                                badge,
                                style: TextStyle(
                                  fontSize: 10,
                                  color: c.accent,
                                  fontWeight: FontWeight.w600,
                                ),
                              ),
                          ],
                        ),
                        const SizedBox(height: 6),
                        Text(
                          itemTitle,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.w600,
                            color: c.textStrong,
                          ),
                        ),
                        if (subtitle != null && subtitle.isNotEmpty)
                          Text(
                            subtitle,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(fontSize: 11, color: c.textMuted),
                          ),
                      ],
                    ),
                  ),
                );
              },
            ),
          ],
        );
      },
    );
  }
}
