import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../design/keys.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

/// Button styles. The order mirrors a C# enum by index; do not reorder.
typedef UiKitButtonStyle = UiMixButtonStyle;

enum UiMixButtonStyle { primary, secondary, ghost, danger, link }

/// Desktop-dense button unified with `Btn` (`controls.dart`):
/// 36px standard / 30px compact height, 14px/10px horizontal padding,
/// 10px radius, 13px w600 Inter label, 15px Lucide icon, 1px press translate,
/// [SelectionRing] focus, and `DESIGN.md` Â§10.8 guard/hint [Tooltip] support.
typedef UiKitButton = UiMixActionButton;

final class UiMixActionButton extends StatefulWidget {
  const UiMixActionButton({
    super.key,
    required this.label,
    this.onPressed,
    this.style = UiMixButtonStyle.primary,
    this.icon,
    this.compact = false,
    this.fullWidth = false,
    this.focusNode,
    this.tooltip,
  });

  final String label;
  final VoidCallback? onPressed;
  final UiMixButtonStyle style;
  final String? icon;
  final bool compact;
  final bool fullWidth;

  /// Optional node for the button's own focus, e.g. a menu trigger.
  final FocusNode? focusNode;

  /// Optional tooltip / `.Guard(rule)` disabled reason shown on hover.
  final String? tooltip;

  @override
  State<UiMixActionButton> createState() => _UiMixActionButtonState();
}

class _UiMixActionButtonState extends State<UiMixActionButton> {
  bool _hover = false, _pressed = false, _focused = false;

  bool get _enabled => widget.onPressed != null;

  void _setPressed(bool value) {
    if (_pressed != value) setState(() => _pressed = value);
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final hover = _hover && _enabled;
    final (Color? fill, Color ink, Color? borderColor) = switch (widget.style) {
      UiMixButtonStyle.primary => (
        hover ? c.accentHover : c.accent,
        c.onAccent,
        null,
      ),
      UiMixButtonStyle.secondary => (
        hover ? c.surfaceSunk : c.surfaceCard,
        c.textStrong,
        c.borderInput,
      ),
      UiMixButtonStyle.ghost => (
        hover ? c.surfaceSunk : Colors.transparent,
        c.textBody,
        null,
      ),
      UiMixButtonStyle.danger => (
        hover ? c.errorBg : Colors.transparent,
        c.textStrong,
        c.error,
      ),
      UiMixButtonStyle.link => (
        null,
        hover ? c.accentCopyHover : c.accentCopy,
        null,
      ),
    };
    final icon = widget.icon == null ? null : uiIcon(widget.icon!);
    final targetHeight = widget.compact
        ? tokens.buttonSmallHeight
        : tokens.buttonHeight;
    final hPad = widget.compact ? 10.0 : 14.0;

    Widget body = Row(
      mainAxisSize: widget.fullWidth ? MainAxisSize.max : MainAxisSize.min,
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        if (icon != null) ...[
          Icon(icon, size: 15, color: ink),
          const SizedBox(width: 6),
        ],
        Flexible(
          child: Text(
            widget.label,
            style: UiType.textStyle(13, weight: FontWeight.w600, color: ink),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            textAlign: TextAlign.center,
          ),
        ),
      ],
    );

    if (widget.style != UiMixButtonStyle.link) {
      final ringActive = _focused;
      var boxStyle = BoxStyler()
          .padding(EdgeInsetsGeometryMix.symmetric(horizontal: hPad))
          .borderRadius(BorderRadiusGeometryMix.circular(tokens.button))
          .animate(
            AnimationConfig.curve(
              duration: tokens.fast,
              curve: tokens.ease,
            ),
          );
      if (fill != null) {
        boxStyle = boxStyle.color(fill);
      }
      if (ringActive) {
        boxStyle = boxStyle.border(
          BoxBorderMix.all(BorderSideMix.color(c.accent).width(1.5)),
        );
      } else if (borderColor != null) {
        boxStyle = boxStyle.border(
          BoxBorderMix.all(BorderSideMix.color(borderColor).width(1.0)),
        );
      }
      final decorated = Box(
        style: boxStyle,
        child: Center(widthFactor: 1, heightFactor: 1, child: body),
      );
      body = LayoutBuilder(
        builder: (context, constraints) {
          final h = constraints.maxHeight < targetHeight
              ? constraints.maxHeight
              : targetHeight;
          return SizedBox(height: h, child: decorated);
        },
      );
    }
    if (widget.fullWidth) body = SizedBox(width: double.infinity, child: body);

    final tip = widget.tooltip?.trim();
    Widget result = Stop(
      onActivate: widget.onPressed,
      isDefault: widget.style == UiMixButtonStyle.primary,
      builder: (context, stopView) => Opacity(
        opacity: _enabled ? 1 : 0.45,
        child: Semantics(
          button: true,
          enabled: _enabled,
          tooltip: (tip != null && tip.isNotEmpty) ? tip : null,
          child: NakedButton(
            focusNode: widget.focusNode,
            enabled: _enabled,
            onPressed: widget.onPressed,
            onHoverChange: (v) {
              if (_hover != v) setState(() => _hover = v);
            },
            onFocusChange: (v) {
              if (_focused != v) setState(() => _focused = v);
            },
            onPressChange: _setPressed,
            child: SelectionRing(
              on: _focused || stopView.selected,
              radius: tokens.button,
              child: Transform.translate(
                offset: Offset(0, _pressed ? 1 : 0),
                child: body,
              ),
            ),
          ),
        ),
      ),
    );

    if (tip != null && tip.isNotEmpty) {
      result = Tooltip(
        message: tip,
        waitDuration: tokens.hintDelay,
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
        decoration: BoxDecoration(
          color: c.isDark ? c.surfaceCard : c.surfaceInk,
          borderRadius: BorderRadius.circular(tokens.xs),
          border: c.isDark ? Border.all(color: c.borderHairline) : null,
          boxShadow: UiShadows.sm(c.surfaceInk),
        ),
        textStyle: UiType.textStyle(
          12,
          weight: FontWeight.w500,
          color: c.isDark ? c.textStrong : c.onInk,
        ),
        child: result,
      );
    }

    return result;
  }
}
