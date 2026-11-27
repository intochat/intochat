import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'layout_primitives.dart';
import 'style.dart';
import 'ui_control_surface.dart';

// --- src/primitives/checkbox.dart ---
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
    this.semanticsId,
  });

  final bool value;
  final ValueChanged<bool>? onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final String? semanticsId;

  @override
  State<UiMixCheckbox> createState() => _UiMixCheckboxState();
}

class _UiMixCheckboxState extends State<UiMixCheckbox> {
  bool _hovered = false;
  bool _pressed = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final isInteractive = widget.enabled && widget.onChanged != null;
    final resolved = widget.style.resolve(
      hovered: _hovered && isInteractive,
      pressed: _pressed && isInteractive,
      focused: _focused && isInteractive,
    );
    final activeBg = UiStyleMapper.resolveToneBackground(colors, resolved.tone);
    final fillColor = widget.value ? activeBg : colors.recessed;
    final hasExplicitPressTone =
        _pressed && isInteractive && widget.style.press?.tone != null;
    final effectiveFill = !isInteractive
        ? fillColor.withValues(alpha: 0.45)
        : _pressed && !hasExplicitPressTone
        ? fillColor.withValues(alpha: 0.85)
        : fillColor;
    final borderColor = _focused
        ? colors.focus
        : _hovered && isInteractive
        ? colors.accent
        : widget.value
        ? activeBg
        : colors.hairline;
    final duration = resolved.motion.duration;
    final curve = resolved.motion.curve;
    final radius =
        resolved.radius ?? (tokens.controlRadius / 2).clamp(4.0, 6.0);

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
      },
      onPressChange: (pressed) {
        if (_pressed == pressed) return;
        setState(() => _pressed = pressed);
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
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

// --- src/primitives/radio.dart ---
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
    this.semanticsId,
  });

  final T value;
  final T? groupValue;
  final ValueChanged<T>? onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final String? semanticsId;

  @override
  State<UiMixRadio<T>> createState() => _UiMixRadioState<T>();
}

class _UiMixRadioState<T> extends State<UiMixRadio<T>> {
  bool _hovered = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final isSelected = widget.value == widget.groupValue;
    final isInteractive = widget.enabled && widget.onChanged != null;
    final resolved = widget.style.resolve(
      hovered: _hovered && isInteractive,
      focused: _focused && isInteractive,
    );
    final activeColor = UiStyleMapper.resolveToneBackground(
      colors,
      resolved.tone,
    );
    final borderColor = _focused
        ? colors.focus
        : _hovered && isInteractive
        ? colors.accent
        : isSelected
        ? activeColor
        : colors.hairline;
    final duration = resolved.motion.duration;
    final curve = resolved.motion.curve;

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
        },
        onFocusChange: (focused) {
          if (_focused == focused) return;
          setState(() => _focused = focused);
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

// --- src/primitives/toggle.dart ---
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
    this.semanticsId,
    this.semanticLabel,
  });

  final bool value;
  final ValueChanged<bool>? onChanged;
  final String label;
  final bool enabled;
  final UiStyleData style;
  final String? semanticsId;
  final String? semanticLabel;

  @override
  State<UiMixToggle> createState() => _UiMixToggleState();
}

class _UiMixToggleState extends State<UiMixToggle> {
  bool _hovered = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final isInteractive = widget.enabled && widget.onChanged != null;
    final resolved = widget.style.resolve(
      hovered: _hovered && isInteractive,
      focused: _focused && isInteractive,
    );
    final activeBg = UiStyleMapper.resolveToneBackground(colors, resolved.tone);
    final trackColor = widget.value ? activeBg : colors.recessed;
    final duration = resolved.motion.duration;
    final curve = resolved.motion.curve;

    final toggle = NakedToggle(
      value: widget.value,
      enabled: isInteractive,
      asSwitch: true,
      semanticLabel:
          widget.semanticLabel ??
          (widget.label.isEmpty ? 'Toggle' : widget.label),
      onChanged: isInteractive ? widget.onChanged : null,
      onHoverChange: (hovered) {
        if (_hovered == hovered) return;
        setState(() => _hovered = hovered);
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
      },
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Box(
            key: const ValueKey('ui_toggle_track'),
            style: BoxStyler()
                .width(40)
                .height(22)
                .padding(EdgeInsetsGeometryMix.all(2))
                .color(
                  isInteractive
                      ? trackColor
                      : trackColor.withValues(alpha: 0.45),
                )
                .borderRadius(
                  BorderRadiusGeometryMix.circular(resolved.radius ?? 999),
                )
                .border(
                  BoxBorderMix.all(
                    BorderSideMix.color(
                      _focused
                          ? colors.focus
                          : _hovered && isInteractive
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
            SizedBox(width: resolved.gap ?? 10),
            Text(
              widget.label,
              style: TextStyle(
                color: isInteractive ? colors.strong : colors.muted,
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

// --- src/primitives/segmented.dart ---
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

// --- src/primitives/select.dart ---
typedef UiKitSelectItem<T> = UiMixSelectItem<T>;

final class UiMixSelectItem<T> {
  const UiMixSelectItem(
    this.value,
    this.label, {
    this.enabled = true,
    this.detail,
  });

  final T value;
  final String label;
  final bool enabled;
  final String? detail;
}

/// Figma select: a full-width 48 high box, radius 12, `inputBorder`, with a
/// 3 px `focus` ring. A null [onChanged] disables it. Lists over eight
/// items get a search box.
///
/// [itemBuilder] replaces the default row (label and detail); it receives the
/// width available for the row. [triggerBuilder] replaces the box, for
/// callers that supply their own trigger.
typedef UiKitSelect<T> = UiMixSelect<T>;

final class UiMixSelect<T> extends StatefulWidget {
  const UiMixSelect({
    super.key,
    required this.value,
    required this.items,
    required this.onChanged,
    this.label,
    this.hint,
    this.itemBuilder,
    this.triggerBuilder,
  });

  final T? value;
  final List<UiMixSelectItem<T>> items;
  final ValueChanged<T>? onChanged;
  final String? label;
  final String? hint;
  final Widget Function(
    BuildContext context,
    UiMixSelectItem<T> item,
    double width,
  )?
  itemBuilder;
  final Widget Function(BuildContext context, MenuController menu)?
  triggerBuilder;

  @override
  State<UiMixSelect<T>> createState() => _UiMixSelectState<T>();
}

class _UiMixSelectState<T> extends State<UiMixSelect<T>> {
  final _menu = MenuController();
  final _search = TextEditingController();
  final _searchFocus = FocusNode();
  String _query = '';
  bool _focused = false;

  @override
  void dispose() {
    _search.dispose();
    _searchFocus.dispose();
    super.dispose();
  }

  void _choose(UiMixSelectItem<T> item) {
    if (!item.enabled) return;
    _menu.close();
    widget.onChanged?.call(item.value);
  }

  void _toggle() => _menu.isOpen ? _menu.close() : _menu.open();

  @override
  Widget build(BuildContext context) {
    final type = UiType.of(context);
    final matches = widget.items
        .where((item) => item.label.toLowerCase().contains(_query))
        .toList();
    final searchable = widget.items.length > 8;
    final selected = widget.items
        .where((item) => item.value == widget.value)
        .firstOrNull;
    final name = widget.label ?? widget.hint ?? '';
    final enabled = widget.onChanged != null;
    return LayoutBuilder(
      builder: (context, constraints) {
        final full = constraints.maxWidth.isFinite;
        final menuWidth = (full ? constraints.maxWidth : 280.0).clamp(
          240.0,
          480.0,
        );
        // Leading check column and menu item padding.
        final rowWidth = menuWidth - (widget.value == null ? 32 : 64);
        final anchor = MenuAnchor(
          controller: _menu,
          onOpen: () {
            _search.clear();
            setState(() => _query = '');
            if (searchable) {
              WidgetsBinding.instance.addPostFrameCallback((_) {
                if (mounted && _menu.isOpen) _searchFocus.requestFocus();
              });
            }
          },
          menuChildren: [
            SizedBox(
              width: menuWidth,
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  if (searchable)
                    Padding(
                      padding: const EdgeInsets.all(8),
                      child: TextField(
                        controller: _search,
                        focusNode: _searchFocus,
                        decoration: const InputDecoration(
                          labelText: 'Search options',
                          prefixIcon: Icon(Icons.search),
                          isDense: true,
                        ),
                        onChanged: (v) =>
                            setState(() => _query = v.trim().toLowerCase()),
                        onSubmitted: (_) {
                          final first = matches
                              .where((item) => item.enabled)
                              .firstOrNull;
                          if (first != null) _choose(first);
                        },
                      ),
                    ),
                  ConstrainedBox(
                    constraints: BoxConstraints(
                      maxHeight: MediaQuery.sizeOf(context).height * .55,
                    ),
                    child: SingleChildScrollView(
                      primary: false,
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          if (matches.isEmpty)
                            Padding(
                              padding: const EdgeInsets.all(16),
                              child: Semantics(
                                liveRegion: true,
                                child: const Text('No matching options'),
                              ),
                            ),
                          for (final item in matches)
                            MenuItemButton(
                              onPressed: item.enabled
                                  ? () => _choose(item)
                                  : null,
                              leadingIcon: widget.value == null
                                  ? null
                                  : SizedBox(
                                      width: 20,
                                      child: item.value == widget.value
                                          ? const Icon(Icons.check, size: 18)
                                          : null,
                                    ),
                              child:
                                  widget.itemBuilder?.call(
                                    context,
                                    item,
                                    rowWidth,
                                  ) ??
                                  _row(item, type, rowWidth),
                            ),
                        ],
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ],
          builder: (context, controller, _) =>
              widget.triggerBuilder?.call(context, controller) ??
              _box(context, selected, full),
        );
        final field = Semantics(
          button: true,
          enabled: enabled,
          label: name,
          child: NakedButton(
            enabled: enabled,
            onFocusChange: (v) {
              if (_focused != v) setState(() => _focused = v);
            },
            onPressed: enabled ? _toggle : null,
            child: Opacity(opacity: enabled ? 1 : 0.45, child: anchor),
          ),
        );
        return Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: full && widget.triggerBuilder == null
              ? CrossAxisAlignment.stretch
              : CrossAxisAlignment.start,
          children: [
            if (widget.label != null && widget.label!.isNotEmpty) ...[
              ExcludeSemantics(
                child: Text(widget.label!, style: type.fieldLabel),
              ),
              const SizedBox(height: 7),
            ],
            field,
          ],
        );
      },
    );
  }

  Widget _box(BuildContext context, UiMixSelectItem<T>? selected, bool full) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    final tokens = UiTokens.of(context);
    final radius = tokens.fieldRadius;
    final shadows = UiShadows.xs(uiKitShadowBase(context));

    var boxStyle = BoxStyler()
        .color(c.surface)
        .height(48)
        .padding(EdgeInsetsGeometryMix.symmetric(horizontal: 14))
        .borderRadius(BorderRadiusGeometryMix.circular(radius))
        .border(
          BoxBorderMix.all(
            BorderSideMix.color(_focused ? c.accent : c.inputBorder).width(1.0),
          ),
        )
        .animate(
          AnimationConfig.curve(duration: tokens.fast, curve: tokens.ease),
        );
    if (full) {
      boxStyle = boxStyle.width(double.infinity);
    }
    for (final s in shadows) {
      boxStyle = boxStyle.shadow(
        BoxShadowMix(
          color: s.color,
          blurRadius: s.blurRadius,
          spreadRadius: s.spreadRadius,
          offset: s.offset,
        ),
      );
    }

    final box = Box(
      key: const ValueKey('ui_kit_select_box'),
      style: boxStyle,
      child: Row(
        mainAxisSize: full ? MainAxisSize.max : MainAxisSize.min,
        children: [
          Flexible(
            child: Text(
              selected?.label ?? widget.hint ?? '',
              overflow: TextOverflow.ellipsis,
              style: type.body.copyWith(
                color: selected == null ? c.subtle : c.strong,
              ),
            ),
          ),
          const SizedBox(width: 8),
          Icon(Icons.keyboard_arrow_down, size: 18, color: c.muted),
        ],
      ),
    );
    return Stack(
      clipBehavior: Clip.none,
      children: [
        if (_focused)
          Positioned(
            left: -3,
            top: -3,
            right: -3,
            bottom: -3,
            child: IgnorePointer(
              child: Box(
                key: const ValueKey('ui_kit_select_ring'),
                style: BoxStyler()
                    .borderRadius(BorderRadiusGeometryMix.circular(radius + 3))
                    .border(
                      BoxBorderMix.all(BorderSideMix.color(c.focus).width(3.0)),
                    ),
              ),
            ),
          ),
        box,
      ],
    );
  }

  Widget _row(UiMixSelectItem<T> item, UiTypeSet type, double width) =>
      SizedBox(
        width: width,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(item.label, style: type.menuItem),
            if (item.detail != null) Text(item.detail!, style: type.meta),
          ],
        ),
      );
}

// --- src/primitives/slider.dart ---
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
  final String? semanticsId;

  @override
  State<UiMixSlider> createState() => _UiMixSliderState();
}

class _UiMixSliderState extends State<UiMixSlider> {
  bool _hovered = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final isInteractive = widget.enabled && widget.onChanged != null;
    final safeMax = widget.max > widget.min ? widget.max : widget.min + 1.0;
    final safeStep = widget.step > 0 && widget.step.isFinite
        ? widget.step
        : 1.0;
    final clamped = widget.value.clamp(widget.min, safeMax);
    final resolved = widget.style.resolve(
      hovered: _hovered && isInteractive,
      focused: _focused && isInteractive,
    );
    final activeColor = UiStyleMapper.resolveToneBackground(
      colors,
      resolved.tone,
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
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
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
