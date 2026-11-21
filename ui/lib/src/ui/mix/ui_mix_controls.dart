import 'dart:async';

import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_icon.dart';
import '../../theme/ui_tokens.dart';
import '../ui_control_surface.dart';
import 'ui_mix_style.dart';

/// Option B `naked_ui` (`NakedToggle(asSwitch: true)`) + `mix` toggle control
/// with exact C# [UiStyleData] parity, 120fps spring thumb motion, and opt-in
/// WinForms-style [UiEventMask] signal dispatch.
final class UiMixToggle extends StatefulWidget {
  const UiMixToggle({
    super.key,
    required this.value,
    required this.onChanged,
    this.label = '',
    this.enabled = true,
    this.style = const UiStyleData(
      tone: 'accent',
      motion: UiMotionPreset.spring,
    ),
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
  });

  final bool value;
  final ValueChanged<bool> onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;

  @override
  State<UiMixToggle> createState() => _UiMixToggleState();
}

class _UiMixToggleState extends State<UiMixToggle> {
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
    final activeBg = UiStyleMapper.resolveToneBackground(
      colors,
      widget.style.tone,
    );
    final trackColor = widget.value ? activeBg : colors.recessed;
    final duration = widget.style.motion.duration;
    final curve = widget.style.motion.curve;

    final toggle = NakedToggle(
      value: widget.value,
      enabled: widget.enabled,
      asSwitch: true,
      semanticLabel: widget.label.isEmpty ? 'Toggle' : widget.label,
      onChanged: widget.onChanged,
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
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Box(
            style: BoxStyler()
                .width(42)
                .height(24)
                .padding(EdgeInsetsGeometryMix.all(3))
                .color(
                  widget.enabled
                      ? trackColor
                      : trackColor.withValues(alpha: 0.45),
                )
                .borderRadius(BorderRadiusGeometryMix.circular(999))
                .border(
                  BoxBorderMix.all(
                    BorderSideMix.color(
                      _focused
                          ? colors.focus
                          : _hovered
                          ? colors.accent
                          : colors.hairline,
                    ).width(_focused ? 1.5 : 1.0),
                  ),
                )
                .animate(
                  AnimationConfig.curve(duration: duration, curve: curve),
                ),
            child: AnimatedAlign(
              duration: duration,
              curve: curve,
              alignment: widget.value
                  ? Alignment.centerRight
                  : Alignment.centerLeft,
              child: Container(
                width: 16,
                height: 16,
                decoration: const BoxDecoration(
                  color: Colors.white,
                  shape: BoxShape.circle,
                ),
              ),
            ),
          ),
          if (widget.label.isNotEmpty) ...[
            SizedBox(width: widget.style.gap ?? 10),
            Text(
              widget.label,
              style: TextStyle(
                color: widget.enabled ? colors.strong : colors.muted,
                fontSize: 13.5,
                fontWeight: FontWeight.w500,
              ),
            ),
          ],
        ],
      ),
    );

    if (widget.semanticsId == null) return toggle;
    return Semantics(
      identifier: widget.semanticsId,
      toggled: widget.value,
      child: toggle,
    );
  }
}

/// Option B `naked_ui` + `mix` Expander control with smooth spring chevron
/// rotation, animated body reveal, exact C# [UiStyleData] padding/radius
/// parity, and WinForms-style [UiEventMask] signals.
final class UiMixExpander extends StatefulWidget {
  const UiMixExpander({
    super.key,
    required this.header,
    required this.isExpanded,
    required this.onExpandedChanged,
    required this.body,
    this.subtitle = '',
    this.enabled = true,
    this.style = const UiStyleData(
      tone: 'card',
      paddingX: 14,
      paddingY: 12,
      radius: 10,
      motion: UiMotionPreset.standard,
    ),
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
  });

  final String header;
  final String subtitle;
  final bool isExpanded;
  final ValueChanged<bool> onExpandedChanged;
  final Widget body;
  final bool enabled;
  final UiStyleData style;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;

  @override
  State<UiMixExpander> createState() => _UiMixExpanderState();
}

class _UiMixExpanderState extends State<UiMixExpander> {
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
    final duration = widget.style.motion.duration;
    final curve = widget.style.motion.curve;

    final card = Box(
      style: UiStyleMapper.boxStyler(
        context,
        widget.style,
        isHovered: _hovered,
        isFocused: _focused,
        isEnabled: widget.enabled,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          NakedButton(
            enabled: widget.enabled,
            onPressed: () => widget.onExpandedChanged(!widget.isExpanded),
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
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        widget.header,
                        style: TextStyle(
                          color: colors.strong,
                          fontSize: 14,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                      if (widget.subtitle.isNotEmpty)
                        Text(
                          widget.subtitle,
                          style: TextStyle(color: colors.muted, fontSize: 12),
                        ),
                    ],
                  ),
                ),
                AnimatedRotation(
                  turns: widget.isExpanded ? 0.5 : 0.0,
                  duration: duration,
                  curve: curve,
                  child: Icon(
                    uiIcon('chevron-down') ?? Icons.expand_more,
                    size: 18,
                    color: colors.muted,
                  ),
                ),
              ],
            ),
          ),
          AnimatedSize(
            duration: duration,
            curve: curve,
            alignment: Alignment.topCenter,
            child: widget.isExpanded
                ? Padding(
                    padding: EdgeInsets.only(top: widget.style.gap ?? 10),
                    child: widget.body,
                  )
                : const SizedBox.shrink(),
          ),
        ],
      ),
    );

    if (widget.semanticsId == null) return card;
    return Semantics(
      identifier: widget.semanticsId,
      expanded: widget.isExpanded,
      child: card,
    );
  }
}

/// Option B `naked_ui` (`NakedCheckbox`) + `mix` Checkbox control with exact
/// C# [UiStyleData] parity, spring check reveal, and opt-in WinForms-style
/// [UiEventMask] signal dispatch.
final class UiMixCheckbox extends StatefulWidget {
  const UiMixCheckbox({
    super.key,
    required this.value,
    required this.onChanged,
    this.label = '',
    this.enabled = true,
    this.style = const UiStyleData(
      tone: 'accent',
      motion: UiMotionPreset.spring,
    ),
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
  });

  final bool value;
  final ValueChanged<bool>? onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;

  @override
  State<UiMixCheckbox> createState() => _UiMixCheckboxState();
}

class _UiMixCheckboxState extends State<UiMixCheckbox> {
  bool _hovered = false;
  bool _pressed = false;
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
    final activeBg = UiStyleMapper.resolveToneBackground(
      colors,
      widget.style.tone,
    );
    final fillColor = widget.value ? activeBg : colors.recessed;
    final effectiveFill = !widget.enabled
        ? fillColor.withValues(alpha: 0.45)
        : _pressed
        ? fillColor.withValues(alpha: 0.85)
        : fillColor;
    final borderColor = _focused
        ? colors.focus
        : _hovered
        ? colors.accent
        : widget.value
        ? activeBg
        : colors.hairline;
    final duration = widget.style.motion.duration;
    final curve = widget.style.motion.curve;
    final radius =
        widget.style.radius ?? (tokens.controlRadius / 2).clamp(4.0, 6.0);

    final checkbox = NakedCheckbox(
      value: widget.value,
      enabled: widget.enabled && widget.onChanged != null,
      semanticLabel: widget.label.isEmpty ? 'Checkbox' : widget.label,
      onChanged: widget.enabled && widget.onChanged != null
          ? (next) => widget.onChanged!(next ?? !widget.value)
          : null,
      onHoverChange: (hovered) {
        if (_hovered == hovered) return;
        setState(() => _hovered = hovered);
        _emit(
          UiEventMask.hover,
          hovered ? 'OnPointerEnter' : 'OnPointerLeave',
          const [{}],
        );
      },
      onPressChange: (pressed) {
        if (_pressed == pressed) return;
        setState(() => _pressed = pressed);
        _emit(
          UiEventMask.press,
          pressed ? 'OnPointerDown' : 'OnPointerUp',
          const [{}],
        );
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
        _emit(UiEventMask.focus, 'OnFocusChanged', [focused]);
      },
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Box(
            style: BoxStyler()
                .width(18)
                .height(18)
                .color(effectiveFill)
                .borderRadius(BorderRadiusGeometryMix.circular(radius))
                .border(
                  BoxBorderMix.all(
                    BorderSideMix.color(borderColor)
                        .width(_focused ? 1.5 : 1.0),
                  ),
                )
                .animate(
                  AnimationConfig.curve(duration: duration, curve: curve),
                ),
            child: Center(
              child: AnimatedScale(
                scale: widget.value ? 1.0 : 0.0,
                duration: duration,
                curve: curve,
                child: Icon(
                  uiIcon('check') ?? Icons.check,
                  size: 12,
                  color: Colors.white,
                ),
              ),
            ),
          ),
          if (widget.label.isNotEmpty) ...[
            SizedBox(width: widget.style.gap ?? tokens.spaceSmall),
            Flexible(
              child: Text(
                widget.label,
                style: TextStyle(
                  color: widget.enabled ? colors.strong : colors.muted,
                  fontSize: 13.5,
                  fontWeight: FontWeight.w500,
                ),
              ),
            ),
          ],
        ],
      ),
    );

    if (widget.semanticsId == null) return checkbox;
    return Semantics(
      identifier: widget.semanticsId,
      checked: widget.value,
      enabled: widget.enabled,
      child: checkbox,
    );
  }
}

/// Visual elevation tier for [UiMixSurface].
enum UiMixSurfaceVariant { card, elevated, recessed }

/// Option B `mix` Surface primitive driven by [UiColors] and [UiTokens] for
/// card, elevated, and recessed containers.
final class UiMixSurface extends StatelessWidget {
  const UiMixSurface({
    super.key,
    required this.child,
    this.variant = UiMixSurfaceVariant.card,
    this.padding,
    this.radius,
    this.style,
    this.semanticsId,
  });

  final Widget child;
  final UiMixSurfaceVariant variant;
  final EdgeInsetsGeometry? padding;
  final double? radius;
  final UiStyleData? style;
  final String? semanticsId;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);

    final bg = switch (variant) {
      UiMixSurfaceVariant.card => colors.surface,
      UiMixSurfaceVariant.elevated => colors.surface,
      UiMixSurfaceVariant.recessed => colors.recessed,
    };

    final effectiveRadius = radius ?? style?.radius ?? tokens.card;
    final px = style?.paddingX ?? tokens.spaceMedium;
    final py = style?.paddingY ?? tokens.spaceMedium;

    var boxStyle = BoxStyler()
        .color(bg)
        .borderRadius(BorderRadiusGeometryMix.circular(effectiveRadius))
        .border(
          BoxBorderMix.all(BorderSideMix.color(colors.hairline).width(1.0)),
        );

    if (padding == null) {
      boxStyle = boxStyle.padding(
        EdgeInsetsGeometryMix.symmetric(horizontal: px, vertical: py),
      );
    }

    if (variant == UiMixSurfaceVariant.elevated) {
      boxStyle = boxStyle.shadow(
        BoxShadowMix(
          color: colors.hairline.withValues(alpha: 0.35),
          blurRadius: 12,
          offset: const Offset(0, 4),
        ),
      );
    }

    final surface = Box(
      style: boxStyle,
      child: padding != null ? Padding(padding: padding!, child: child) : child,
    );

    if (semanticsId == null) return surface;
    return Semantics(identifier: semanticsId, child: surface);
  }
}

/// Option B `naked_ui` (`NakedButton`) + `mix` IconButton control with
/// hover/press/focus states, optional tooltip, and [UiEventMask] signals.
final class UiMixIconButton extends StatefulWidget {
  const UiMixIconButton({
    super.key,
    required this.icon,
    required this.onPressed,
    this.iconSize = 16,
    this.iconColor,
    this.tooltip,
    this.size = 30,
    this.enabled = true,
    this.style = const UiStyleData(tone: 'subtle', paddingX: 6, paddingY: 6),
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
  });

  final IconData icon;
  final VoidCallback? onPressed;
  final double iconSize;
  final Color? iconColor;
  final String? tooltip;
  final double size;
  final bool enabled;
  final UiStyleData style;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;

  @override
  State<UiMixIconButton> createState() => _UiMixIconButtonState();
}

class _UiMixIconButtonState extends State<UiMixIconButton> {
  bool _hovered = false;
  bool _pressed = false;
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
    final isInteractive = widget.enabled && widget.onPressed != null;
    final radius = widget.style.radius ?? tokens.controlRadius;
    final bg = !isInteractive
        ? Colors.transparent
        : _pressed
        ? colors.recessed
        : _hovered
        ? colors.recessed.withValues(alpha: 0.7)
        : Colors.transparent;
    final borderColor = _focused
        ? colors.focus
        : _hovered && isInteractive
        ? colors.hairline
        : Colors.transparent;
    final fg =
        widget.iconColor ?? (isInteractive ? colors.strong : colors.muted);

    Widget button = NakedButton(
      enabled: isInteractive,
      onPressed: widget.onPressed,
      semanticLabel: widget.tooltip,
      onHoverChange: (hovered) {
        if (_hovered == hovered) return;
        setState(() => _hovered = hovered);
        _emit(
          UiEventMask.hover,
          hovered ? 'OnPointerEnter' : 'OnPointerLeave',
          const [{}],
        );
      },
      onPressChange: (pressed) {
        if (_pressed == pressed) return;
        setState(() => _pressed = pressed);
        _emit(
          UiEventMask.press,
          pressed ? 'OnPointerDown' : 'OnPointerUp',
          const [{}],
        );
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
        _emit(UiEventMask.focus, 'OnFocusChanged', [focused]);
      },
      child: AnimatedScale(
        scale: _pressed && isInteractive ? 0.94 : 1.0,
        duration: widget.style.motion.duration,
        curve: widget.style.motion.curve,
        child: Box(
          style: BoxStyler()
              .width(widget.size)
              .height(widget.size)
              .color(bg)
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
          child: Center(
            child: Icon(
              widget.icon,
              size: widget.iconSize,
              color: isInteractive ? fg : fg.withValues(alpha: 0.45),
            ),
          ),
        ),
      ),
    );

    if (widget.tooltip != null && widget.tooltip!.isNotEmpty) {
      button = Tooltip(message: widget.tooltip!, child: button);
    }

    if (widget.semanticsId == null) return button;
    return Semantics(
      identifier: widget.semanticsId,
      button: true,
      enabled: isInteractive,
      child: button,
    );
  }
}

/// Option B `naked_ui` (`NakedRadio<T>`) + `mix` Radio control with spring dot
/// reveal, [UiColors]/[UiTokens] styling, and [UiEventMask] signals.
final class UiMixRadio<T> extends StatefulWidget {
  const UiMixRadio({
    super.key,
    required this.value,
    required this.groupValue,
    required this.onChanged,
    this.label = '',
    this.enabled = true,
    this.style = const UiStyleData(
      tone: 'accent',
      motion: UiMotionPreset.spring,
    ),
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
  });

  final T value;
  final T? groupValue;
  final ValueChanged<T>? onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;

  @override
  State<UiMixRadio<T>> createState() => _UiMixRadioState<T>();
}

class _UiMixRadioState<T> extends State<UiMixRadio<T>> {
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
    final isSelected = widget.value == widget.groupValue;
    final isInteractive = widget.enabled && widget.onChanged != null;
    final activeColor = UiStyleMapper.resolveToneBackground(
      colors,
      widget.style.tone,
    );
    final borderColor = _focused
        ? colors.focus
        : _hovered && isInteractive
        ? colors.accent
        : isSelected
        ? activeColor
        : colors.hairline;
    final duration = widget.style.motion.duration;
    final curve = widget.style.motion.curve;

    final radio = RadioGroup<T>(
      groupValue: widget.groupValue,
      onChanged: (next) {
        if (isInteractive && next != null) widget.onChanged!(next);
      },
      child: NakedRadio<T>(
        value: widget.value,
        enabled: isInteractive,
        semanticLabel: widget.label.isEmpty ? '${widget.value}' : widget.label,
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
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Box(
              style: BoxStyler()
                  .width(18)
                  .height(18)
                  .color(colors.recessed)
                  .borderRadius(BorderRadiusGeometryMix.circular(999))
                  .border(
                    BoxBorderMix.all(
                      BorderSideMix.color(borderColor)
                          .width(_focused || isSelected ? 1.75 : 1.0),
                    ),
                  )
                  .animate(
                    AnimationConfig.curve(duration: duration, curve: curve),
                  ),
              child: Center(
                child: AnimatedScale(
                  scale: isSelected ? 1.0 : 0.0,
                  duration: duration,
                  curve: curve,
                  child: Container(
                    width: 8,
                    height: 8,
                    decoration: BoxDecoration(
                      color: isInteractive
                          ? activeColor
                          : activeColor.withValues(alpha: 0.45),
                      shape: BoxShape.circle,
                    ),
                  ),
                ),
              ),
            ),
            if (widget.label.isNotEmpty) ...[
              SizedBox(width: widget.style.gap ?? tokens.spaceSmall),
              Flexible(
                child: Text(
                  widget.label,
                  style: TextStyle(
                    color: isInteractive ? colors.body : colors.muted,
                    fontSize: 13.5,
                  ),
                ),
              ),
            ],
          ],
        ),
      ),
    );

    if (widget.semanticsId == null) return radio;
    return Semantics(
      identifier: widget.semanticsId,
      selected: isSelected,
      enabled: isInteractive,
      child: radio,
    );
  }
}

/// Option B `naked_ui` (`NakedSlider`) + `mix` Slider control with smooth
/// track fill, draggable thumb, and [UiColors]/[UiTokens] styling.
final class UiMixSlider extends StatefulWidget {
  const UiMixSlider({
    super.key,
    required this.value,
    this.min = 0.0,
    this.max = 100.0,
    this.step = 1.0,
    required this.onChanged,
    this.onChangeEnd,
    this.enabled = true,
    this.style = const UiStyleData(
      tone: 'accent',
      motion: UiMotionPreset.standard,
    ),
    this.eventMask = UiEventMask.click,
    this.onEmit,
    this.semanticsId,
  });

  final double value;
  final double min;
  final double max;
  final double step;
  final ValueChanged<double>? onChanged;
  final ValueChanged<double>? onChangeEnd;
  final bool enabled;
  final UiStyleData style;
  final int eventMask;
  final UiNeuronEventEmitter? onEmit;
  final String? semanticsId;

  @override
  State<UiMixSlider> createState() => _UiMixSliderState();
}

class _UiMixSliderState extends State<UiMixSlider> {
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
    final isInteractive = widget.enabled && widget.onChanged != null;
    final safeMax = widget.max > widget.min ? widget.max : widget.min + 1.0;
    final safeStep = widget.step > 0 && widget.step.isFinite
        ? widget.step
        : 1.0;
    final clamped = widget.value.clamp(widget.min, safeMax);
    final activeColor = UiStyleMapper.resolveToneBackground(
      colors,
      widget.style.tone,
    );

    final slider = NakedSlider(
      values: [clamped],
      min: widget.min,
      max: safeMax,
      step: safeStep,
      enabled: isInteractive,
      onChanged: isInteractive ? (vals) => widget.onChanged!(vals.first) : null,
      onChangeEnd: isInteractive && widget.onChangeEnd != null
          ? (vals) => widget.onChangeEnd!(vals.first)
          : null,
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
      builder: (context, state, _) {
        final fraction = state.visualPercentageAt(0).clamp(0.0, 1.0);
        return SizedBox(
          height: 28,
          child: LayoutBuilder(
            builder: (context, constraints) {
              final width = constraints.maxWidth.isFinite
                  ? constraints.maxWidth
                  : 200.0;
              const thumbSize = 16.0;
              final trackWidth = (width - thumbSize).clamp(
                0.0,
                double.infinity,
              );
              final thumbLeft = trackWidth * fraction;

              return Stack(
                alignment: Alignment.centerLeft,
                children: [
                  Padding(
                    padding: const EdgeInsets.symmetric(
                      horizontal: thumbSize / 2,
                    ),
                    child: NakedSliderTrack(
                      child: Box(
                        style: BoxStyler()
                            .height(4)
                            .color(colors.recessed)
                            .borderRadius(
                              BorderRadiusGeometryMix.circular(999),
                            ),
                        child: Align(
                          alignment: Alignment.centerLeft,
                          child: FractionallySizedBox(
                            widthFactor: fraction,
                            child: Box(
                              style: BoxStyler()
                                  .height(4)
                                  .color(
                                    isInteractive
                                        ? activeColor
                                        : activeColor.withValues(alpha: 0.45),
                                  )
                                  .borderRadius(
                                    BorderRadiusGeometryMix.circular(999),
                                  ),
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
                  Positioned(
                    left: thumbLeft,
                    child: Box(
                      style: BoxStyler()
                          .width(thumbSize)
                          .height(thumbSize)
                          .color(Colors.white)
                          .borderRadius(BorderRadiusGeometryMix.circular(999))
                          .border(
                            BoxBorderMix.all(
                              BorderSideMix.color(
                                _focused
                                    ? colors.focus
                                    : _hovered || state.isDragging
                                    ? activeColor
                                    : colors.hairline,
                              ).width(_focused || state.isDragging ? 2.0 : 1.5),
                            ),
                          ),
                    ),
                  ),
                ],
              );
            },
          ),
        );
      },
    );

    if (widget.semanticsId == null) return slider;
    return Semantics(
      identifier: widget.semanticsId,
      enabled: isInteractive,
      child: slider,
    );
  }
}

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
