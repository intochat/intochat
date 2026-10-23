import 'package:flutter/material.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';

/// Figma `.tabs`: a 4 px gap over a 1 px `hairline` bottom border. Each tab
/// has 9x10 padding, 12 px `muted` text and a 2 px transparent bottom
/// border; the active one is `strong`, 700, with an `accent` border. The row
/// scrolls horizontally when it overflows. Indexes in [disabled] are dimmed
/// and ignore taps.
final class UiKitTabs extends StatelessWidget {
  const UiKitTabs({
    super.key,
    required this.labels,
    required this.index,
    required this.onChanged,
    this.disabled = const {},
  });

  final List<String> labels;
  final int index;
  final ValueChanged<int> onChanged;
  final Set<int> disabled;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    return DecoratedBox(
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: c.hairline)),
      ),
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        primary: false,
        child: Row(
          children: [
            for (var i = 0; i < labels.length; i++) ...[
              if (i > 0) const SizedBox(width: 4),
              _tab(c, type, i),
            ],
          ],
        ),
      ),
    );
  }

  Widget _tab(UiColors c, UiTypeSet type, int i) {
    final active = i == index;
    final off = disabled.contains(i);
    return Semantics(
      button: true,
      selected: active,
      enabled: !off,
      child: FocusableActionDetector(
        enabled: !off,
        mouseCursor: off ? SystemMouseCursors.basic : SystemMouseCursors.click,
        actions: {
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) => onChanged(i),
          ),
        },
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: off ? null : () => onChanged(i),
          child: Opacity(
            opacity: off ? 0.45 : 1,
            child: DecoratedBox(
              key: ValueKey('ui_kit_tab_$i'),
              decoration: BoxDecoration(
                border: Border(
                  bottom: BorderSide(
                    width: 2,
                    color: active ? c.accent : Colors.transparent,
                  ),
                ),
              ),
              child: Padding(
                padding: const EdgeInsets.symmetric(
                  vertical: 9,
                  horizontal: 10,
                ),
                child: Text(
                  labels[i],
                  style: type.body.copyWith(
                    fontSize: 12,
                    color: active ? c.strong : c.muted,
                    fontWeight: active ? FontWeight.w700 : FontWeight.w400,
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
