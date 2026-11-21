import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../mix/ui_mix_controls.dart';
import '../neuron_widget.dart';

/// Reactive WinUI 3 PipsPager presenter for `IntoChat.OS.Modules.UI.PipsPager.IPipsPager`.
class PipsPagerNeuronWidget extends StatelessWidget {
  const PipsPagerNeuronWidget({
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
      kind: 'ui.pips-pager',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final pageCount = math.max(
          1,
          (state['pageCount'] as num?)?.toInt() ?? 1,
        );
        final selected = ((state['selectedIndex'] as num?)?.toInt() ?? 0).clamp(
          0,
          pageCount - 1,
        );
        final maxVisible = math.max(
          1,
          (state['maxVisiblePips'] as num?)?.toInt() ?? 5,
        );
        final isVertical =
            (state['orientation'] as String?)?.toLowerCase() == 'vertical';
        final showPrevNext = state['showPrevNext'] != false;

        final windowCount = math.min(pageCount, maxVisible);
        final half = windowCount ~/ 2;
        final start = (selected - half).clamp(0, pageCount - windowCount);
        final end = start + windowCount;

        final prevButton = UiMixIconButton(
          tooltip: 'Previous page',
          size: 28,
          enabled: selected > 0,
          onPressed: selected > 0
              ? () => unawaited(invoke('Previous', const []))
              : null,
          icon: isVertical ? LucideIcons.chevronUp : LucideIcons.chevronLeft,
          iconSize: 15,
          iconColor: selected > 0 ? c.textStrong : c.textSubtle,
        );

        final nextButton = UiMixIconButton(
          tooltip: 'Next page',
          size: 28,
          enabled: selected < pageCount - 1,
          onPressed: selected < pageCount - 1
              ? () => unawaited(invoke('Next', const []))
              : null,
          icon: isVertical ? LucideIcons.chevronDown : LucideIcons.chevronRight,
          iconSize: 15,
          iconColor: selected < pageCount - 1 ? c.textStrong : c.textSubtle,
        );

        final pips = <Widget>[
          for (var i = start; i < end; i++)
            Semantics(
              identifier: 'pip:$i',
              button: true,
              selected: i == selected,
              label: 'Page ${i + 1}',
              child: GestureDetector(
                onTap: () => unawaited(invoke('SelectPage', [i])),
                child: AnimatedContainer(
                  duration: const Duration(milliseconds: 160),
                  margin: const EdgeInsets.all(4),
                  width: isVertical ? 8 : (i == selected ? 18 : 8),
                  height: isVertical ? (i == selected ? 18 : 8) : 8,
                  decoration: BoxDecoration(
                    color: i == selected ? c.accent : c.textSubtle,
                    borderRadius: BorderRadius.circular(99),
                  ),
                ),
              ),
            ),
        ];

        final controls = isVertical
            ? Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (showPrevNext) prevButton,
                  ...pips,
                  if (showPrevNext) nextButton,
                ],
              )
            : Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (showPrevNext) prevButton,
                  ...pips,
                  if (showPrevNext) nextButton,
                  const SizedBox(width: 8),
                  Text(
                    'Page ${selected + 1} of $pageCount',
                    style: TextStyle(fontSize: 11.5, color: c.textMuted),
                  ),
                ],
              );

        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: controls,
        );
      },
    );
  }
}
