import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

/// Single option in a [UiSegmentedControl].
final class UiSegmentedOption<T> {
  const UiSegmentedOption({
    required this.value,
    required this.label,
    this.icon,
    this.enabled = true,
  });

  final T value;
  final String label;
  final IconData? icon;
  final bool enabled;
}

/// Mix-styled pill segmented control with recessed track and elevated active thumb.
final class UiSegmentedControl<T> extends StatefulWidget {
  const UiSegmentedControl({
    super.key,
    required this.options,
    required this.value,
    required this.onChanged,
    this.compact = false,
    this.expand = false,
    this.semanticsId,
  });

  final List<UiSegmentedOption<T>> options;
  final T value;
  final ValueChanged<T>? onChanged;
  final bool compact;
  final bool expand;
  final String? semanticsId;

  @override
  State<UiSegmentedControl<T>> createState() => _UiSegmentedControlState<T>();
}

class _UiSegmentedControlState<T> extends State<UiSegmentedControl<T>> {
  int? _hoveredIndex;
  int? _focusedIndex;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final type = UiType.of(context);
    final isInteractive = widget.onChanged != null;
    final vPad = widget.compact ? tokens.p4 : tokens.p6;
    final hPad = widget.compact ? tokens.p10 : tokens.p12;

    final track = Box(
      style: BoxStyler()
          .color(c.recessed)
          .padding(EdgeInsetsGeometryMix.all(3))
          .borderRadius(BorderRadiusGeometryMix.circular(tokens.pill))
          .border(BoxBorderMix.all(BorderSideMix.color(c.hairline).width(1.0))),
      child: Row(
        mainAxisSize: widget.expand ? MainAxisSize.max : MainAxisSize.min,
        children: [
          for (var i = 0; i < widget.options.length; i++)
            _buildSegment(
              context: context,
              index: i,
              option: widget.options[i],
              c: c,
              tokens: tokens,
              type: type,
              isInteractive: isInteractive,
              vPad: vPad,
              hPad: hPad,
            ),
        ],
      ),
    );

    if (widget.semanticsId == null) return track;
    return Semantics(identifier: widget.semanticsId, child: track);
  }

  Widget _buildSegment({
    required BuildContext context,
    required int index,
    required UiSegmentedOption<T> option,
    required UiColors c,
    required UiTokens tokens,
    required UiTypeSet type,
    required bool isInteractive,
    required double vPad,
    required double hPad,
  }) {
    final selected = option.value == widget.value;
    final enabled = isInteractive && option.enabled;
    final hovered = _hoveredIndex == index && enabled;
    final focused = _focusedIndex == index && enabled;

    var thumbStyle = BoxStyler()
        .padding(
          EdgeInsetsGeometryMix.symmetric(horizontal: hPad, vertical: vPad),
        )
        .borderRadius(BorderRadiusGeometryMix.circular(tokens.pill))
        .color(
          selected
              ? c.surface
              : hovered
              ? c.surface.withValues(alpha: 0.45)
              : Colors.transparent,
        )
        .border(
          BoxBorderMix.all(
            BorderSideMix.color(
              focused
                  ? c.focus
                  : selected
                  ? c.hairline
                  : Colors.transparent,
            ).width(focused ? 1.5 : 1.0),
          ),
        )
        .animate(
          AnimationConfig.curve(duration: tokens.fast, curve: tokens.ease),
        );

    if (selected) {
      thumbStyle = thumbStyle.shadow(
        BoxShadowMix(
          color: c.ink.withValues(alpha: 0.08),
          blurRadius: 4,
          offset: const Offset(0, 1),
        ),
      );
    }

    final fg = selected ? c.strong : c.muted;

    final item = Semantics(
      button: true,
      selected: selected,
      enabled: enabled,
      label: option.label,
      child: NakedButton(
        enabled: enabled,
        onPressed: enabled ? () => widget.onChanged?.call(option.value) : null,
        onHoverChange: (h) {
          if (!mounted) return;
          setState(() {
            if (h) {
              _hoveredIndex = index;
            } else if (_hoveredIndex == index) {
              _hoveredIndex = null;
            }
          });
        },
        onFocusChange: (f) {
          if (!mounted) return;
          setState(() {
            if (f) {
              _focusedIndex = index;
            } else if (_focusedIndex == index) {
              _focusedIndex = null;
            }
          });
        },
        child: Opacity(
          opacity: enabled ? 1.0 : 0.45,
          child: Box(
            key: ValueKey('ui_segmented_option_$index'),
            style: thumbStyle,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                if (option.icon != null) ...[
                  Icon(option.icon, size: 14, color: fg),
                  SizedBox(width: tokens.p6),
                ],
                Flexible(
                  child: Text(
                    option.label,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: type.body.copyWith(
                      fontSize: widget.compact ? 12 : 13,
                      fontWeight: selected ? FontWeight.w600 : FontWeight.w500,
                      color: fg,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );

    return widget.expand ? Expanded(child: item) : item;
  }
}
