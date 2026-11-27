import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../design/keys.dart';
import '../theme/mix_tokens.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'style.dart';

/// Button styles. The order mirrors a C# enum by index; do not reorder.
enum UiMixButtonStyle { primary, secondary, ghost, danger, link }

/// Single authoritative IntoChat button primitive combining:
/// - `NakedButton.builder` + `BoxStyler` (`Mix.Style()` [UiStyleData] support +
///   [UiMixButtonStyle] presets `primary`, `secondary`, `ghost`, `danger`, `link`)
/// - [SelectionRing] + [Stop] keyboard-first focus (`DESIGN.md` §1.11)
/// - `36px` standard / `30px` compact desktop density, `1px` press translate,
///   `DESIGN.md` §10.8 [Tooltip], and Lucide [IconData] or icon name [String].
final class UiMixButton extends StatelessWidget {
  const UiMixButton({
    super.key,
    required this.label,
    this.onPressed,
    this.style,
    this.preset = UiMixButtonStyle.primary,
    this.icon,
    this.iconData,
    this.enabled = true,
    this.compact = false,
    this.fullWidth = false,
    this.focusNode,
    this.tooltip,
    this.semanticsId,
  });

  /// Preset convenience constructor for `primary`, `secondary`, `ghost`,
  /// `danger`, and `link` desktop-dense buttons.
  const UiMixButton.preset({
    super.key,
    required this.label,
    this.onPressed,
    UiMixButtonStyle style = UiMixButtonStyle.primary,
    this.icon,
    this.iconData,
    this.enabled = true,
    this.compact = false,
    this.fullWidth = false,
    this.focusNode,
    this.tooltip,
    this.semanticsId,
  }) : preset = style,
       style = null;

  final String label;
  final VoidCallback? onPressed;

  /// Optional structured C# `Mix.Style()` JSON style. When null, renders using
  /// [preset] with standard `36px` / compact `30px` desktop density.
  final UiStyleData? style;

  /// Preset style used when [style] is null.
  final UiMixButtonStyle preset;

  /// Optional icon name resolved via [uiIcon].
  final String? icon;

  /// Optional Lucide [IconData] (takes precedence over [icon]).
  final IconData? iconData;

  final bool enabled;
  final bool compact;
  final bool fullWidth;

  /// Optional node for the button's own focus, e.g. a menu trigger.
  final FocusNode? focusNode;

  /// Optional tooltip / `.Guard(rule)` disabled reason shown on hover.
  final String? tooltip;

  /// Optional semantics identifier (e.g. `key:mix:save`).
  final String? semanticsId;

  bool get _interactive => enabled && onPressed != null;

  Widget _wrapTooltipAndSemantics(UiColors c, UiTokens tokens, Widget child) {
    var result = child;
    final tip = tooltip?.trim();
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
    if (semanticsId != null) {
      result = Semantics(identifier: semanticsId, child: result);
    }
    return result;
  }

  Widget _buildStructured(
    BuildContext context,
    UiColors colors,
    UiTokens tokens,
    UiStyleData style,
  ) {
    final tip = tooltip?.trim();
    final button = Stop(
      onActivate: _interactive ? onPressed : null,
      isDefault: style.tone == 'accent' || style.tone == 'primary',
      builder: (context, stopView) => NakedButton(
        focusNode: focusNode,
        enabled: _interactive,
        onPressed: onPressed,
        tooltip: (tip != null && tip.isNotEmpty) ? tip : null,
        builder: (context, buttonState, _) {
          final hovered = buttonState.isHovered && _interactive;
          final pressed = buttonState.isPressed && _interactive;
          final focused = buttonState.isFocused && _interactive;
          final resolved = style.resolve(
            hovered: hovered,
            pressed: pressed,
            focused: focused,
          );
          final fg = UiStyleMapper.resolveToneForeground(colors, resolved.tone);
          final effectiveFg = _interactive ? fg : fg.withValues(alpha: 0.5);
          final iconName = icon ?? resolved.icon;
          final resolvedIcon =
              iconData ??
              (iconName != null && iconName.isNotEmpty
                  ? uiIcon(iconName)
                  : null);
          final gap = resolved.gap ?? 6.0;
          final textStyle = UiStyleMapper.textStyle(
            context,
            resolved,
            isEnabled: _interactive,
            colorOverride: fg,
          );

          final labelWidget = Text(
            label,
            style: textStyle,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          );

          final content = resolvedIcon == null
              ? labelWidget
              : Row(
                  mainAxisSize: fullWidth ? MainAxisSize.max : MainAxisSize.min,
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    Icon(resolvedIcon, size: 15, color: effectiveFg),
                    SizedBox(width: gap),
                    Flexible(child: labelWidget),
                  ],
                );

          Widget box = Box(
            style: UiStyleMapper.boxStyler(
              context,
              style,
              isHovered: hovered,
              isPressed: pressed,
              isFocused: focused,
              isEnabled: _interactive,
            ),
            child: fullWidth ? Center(child: content) : content,
          );
          if (fullWidth) {
            box = SizedBox(width: double.infinity, child: box);
          }

          return SelectionRing(
            on: focused || stopView.selected,
            radius: resolved.radius ?? tokens.button,
            child: AnimatedScale(
              scale: UiStyleMapper.resolveScale(
                style,
                isHovered: hovered,
                isPressed: pressed,
                isFocused: focused,
                isEnabled: _interactive,
              ),
              duration: resolved.motion.duration,
              curve: resolved.motion.curve,
              child: box,
            ),
          );
        },
      ),
    );

    return _wrapTooltipAndSemantics(colors, tokens, button);
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final structuredStyle = style;
    if (structuredStyle != null) {
      return _buildStructured(context, c, tokens, structuredStyle);
    }

    final resolvedIcon =
        iconData ?? (icon != null && icon!.isNotEmpty ? uiIcon(icon!) : null);
    final targetHeight = compact
        ? tokens.buttonSmallHeight
        : tokens.buttonHeight;
    final hPad = compact ? 10.0 : 14.0;
    final tip = tooltip?.trim();

    final result = Stop(
      onActivate: _interactive ? onPressed : null,
      isDefault: preset == UiMixButtonStyle.primary,
      builder: (context, stopView) => Opacity(
        opacity: _interactive ? 1 : 0.45,
        child: NakedButton(
          focusNode: focusNode,
          enabled: _interactive,
          onPressed: onPressed,
          tooltip: (tip != null && tip.isNotEmpty) ? tip : null,
          builder: (context, buttonState, _) {
            final hover = buttonState.isHovered && _interactive;
            final pressed = buttonState.isPressed && _interactive;
            final focused = buttonState.isFocused && _interactive;
            final (
              Color? fill,
              Color ink,
              Color? borderColor,
            ) = switch (preset) {
              UiMixButtonStyle.primary => (
                hover ? UiMixTokens.accentHover() : UiMixTokens.accent(),
                c.onAccent,
                null,
              ),
              UiMixButtonStyle.secondary => (
                hover ? UiMixTokens.recessed() : UiMixTokens.surface(),
                c.textStrong,
                c.borderInput,
              ),
              UiMixButtonStyle.ghost => (
                hover ? UiMixTokens.recessed() : Colors.transparent,
                c.textBody,
                null,
              ),
              UiMixButtonStyle.danger => (
                hover ? c.errorBg : Colors.transparent,
                c.textStrong,
                UiMixTokens.error(),
              ),
              UiMixButtonStyle.link => (
                null,
                hover ? c.accentCopyHover : c.accentCopy,
                null,
              ),
            };

            Widget body = Row(
              mainAxisSize: fullWidth ? MainAxisSize.max : MainAxisSize.min,
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                if (resolvedIcon != null) ...[
                  Icon(resolvedIcon, size: 15, color: ink),
                  const SizedBox(width: 6),
                ],
                Flexible(
                  child: Text(
                    label,
                    style: UiType.textStyle(
                      13,
                      weight: FontWeight.w600,
                      color: ink,
                    ),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    textAlign: TextAlign.center,
                  ),
                ),
              ],
            );

            if (preset != UiMixButtonStyle.link) {
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
              if (focused) {
                boxStyle = boxStyle.border(
                  BoxBorderMix.all(
                    BorderSideMix.color(UiMixTokens.accent()).width(1.5),
                  ),
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
            if (fullWidth) body = SizedBox(width: double.infinity, child: body);

            return SelectionRing(
              on: focused || stopView.selected,
              radius: tokens.button,
              child: Transform.translate(
                offset: Offset(0, pressed ? 1 : 0),
                child: body,
              ),
            );
          },
        ),
      ),
    );

    return _wrapTooltipAndSemantics(c, tokens, result);
  }
}
