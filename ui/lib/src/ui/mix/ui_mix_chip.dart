import 'dart:async';

import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_tokens.dart';
import '../ui_control_surface.dart';
import 'ui_mix_style.dart';

/// Option B `naked_ui` (`NakedToggle`) + `mix` selectable/filter Chip control.
final class UiMixChip extends StatefulWidget {
  const UiMixChip({
    super.key,
    required this.label,
    required this.selected,
    required this.onSelected,
    this.icon,
    this.enabled = true,
    this.style = const UiStyleData(
      tone: 'accent',
      paddingX: 12,
      paddingY: 6,
      radius: 999,
    ),
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
  });

  final String label;
  final bool selected;
  final ValueChanged<bool>? onSelected;
  final IconData? icon;
  final bool enabled;
  final UiStyleData style;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;

  @override
  State<UiMixChip> createState() => _UiMixChipState();
}

class _UiMixChipState extends State<UiMixChip> {
  bool _hovered = false;
  bool _focused = false;

  void _emit(int flag, String method, List<Object?> args) {
    if (!widget.enabled || (widget.eventMask & flag) == 0) return;
    final emitter = widget.onEmit;
    if (emitter != null) unawaited(emitter(method, args));
  }

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final isInteractive = widget.enabled && widget.onSelected != null;
    final activeBg = UiStyleMapper.resolveToneBackground(
      colors,
      widget.style.tone,
    );
    final bg = widget.selected ? activeBg : colors.recessed;
    final fg = widget.selected
        ? UiStyleMapper.resolveToneForeground(colors, widget.style.tone)
        : colors.strong;
    final borderColor = _focused
        ? colors.focus
        : _hovered && isInteractive
        ? colors.accent
        : widget.selected
        ? activeBg
        : colors.hairline;
    final px = widget.style.paddingX ?? tokens.p12;
    final py = widget.style.paddingY ?? tokens.p6;
    final radius = widget.style.radius ?? tokens.pill;

    final chip = NakedToggle(
      value: widget.selected,
      enabled: isInteractive,
      semanticLabel: widget.label,
      onChanged: isInteractive ? widget.onSelected! : null,
      onHoverChange: (hovered) {
        if (_hovered == hovered) return;
        setState(() => _hovered = hovered);
        _emit(
          UiEventMask.hover,
          hovered ? 'OnPointerEnter' : 'OnPointerLeave',
          const [{}],
        );
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
        _emit(UiEventMask.focus, 'OnFocusChanged', [focused]);
      },
      child: Box(
        style: BoxStyler()
            .color(isInteractive ? bg : bg.withValues(alpha: 0.45))
            .padding(
              EdgeInsetsGeometryMix.symmetric(horizontal: px, vertical: py),
            )
            .borderRadius(BorderRadiusGeometryMix.circular(radius))
            .border(
              BoxBorderMix.all(
                BorderSideMix.color(borderColor).width(_focused ? 1.5 : 1.0),
              ),
            )
            .animate(
              AnimationConfig.curve(
                duration: widget.style.motion.duration,
                curve: widget.style.motion.curve,
              ),
            ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (widget.icon != null) ...[
              Icon(
                widget.icon,
                size: 14,
                color: isInteractive ? fg : fg.withValues(alpha: 0.5),
              ),
              const SizedBox(width: 6),
            ],
            Text(
              widget.label,
              style: TextStyle(
                color: isInteractive ? fg : fg.withValues(alpha: 0.5),
                fontSize: 12.5,
                fontWeight: FontWeight.w500,
              ),
            ),
          ],
        ),
      ),
    );

    if (widget.semanticsId == null) return chip;
    return Semantics(
      identifier: widget.semanticsId,
      selected: widget.selected,
      enabled: isInteractive,
      child: chip,
    );
  }
}
