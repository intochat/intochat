import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/mix_tokens.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'style.dart';
import 'ui_control_surface.dart';

// --- src/primitives/surface.dart ---
Color uiKitShadowBase(BuildContext context) =>
    Theme.of(context).brightness == Brightness.dark
    ? Colors.black
    : UiColors.of(context).strong;

/// Reusable WinUI 3 / visionOS Acrylic Frosted Glass surface driven directly
/// by [UiColors] and [UiTokens].
final class UiAcrylicSurface extends StatelessWidget {
  const UiAcrylicSurface({
    super.key,
    required this.child,
    this.borderRadius,
    this.radius,
    this.padding,
    this.strong = false,
    this.blurSigma,
    this.sigmaOverride,
    this.fillColor,
    this.color,
    this.borderColor,
    this.shadows,
    this.specularTop = false,
  });

  final Widget child;
  final BorderRadius? borderRadius;
  final double? radius;
  final EdgeInsetsGeometry? padding;
  final bool strong;
  final double? blurSigma;
  final double? sigmaOverride;
  final Color? fillColor;
  final Color? color;
  final Color? borderColor;
  final List<BoxShadow>? shadows;
  final bool specularTop;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final cardRadius = UiMixTokens.cardRadius.resolve(context).x;
    final effectiveRadius =
        borderRadius ?? BorderRadius.circular(radius ?? cardRadius);
    final sigma = sigmaOverride ?? blurSigma ?? c.blurSigma;
    final fill =
        color ?? fillColor ?? (strong ? c.surfaceGlassStrong : c.surface);
    final border = borderColor ?? (c.isGlass ? c.specularBorder : c.hairline);

    final box = Container(
      padding: padding,
      decoration: BoxDecoration(
        color: fill,
        borderRadius: effectiveRadius,
        border: Border.all(color: border),
        boxShadow: shadows,
      ),
      child: child,
    );

    if (sigma <= 0) return box;

    return ClipRRect(
      borderRadius: effectiveRadius,
      child: BackdropFilter(
        filter: ImageFilter.blur(sigmaX: sigma, sigmaY: sigma),
        child: box,
      ),
    );
  }
}

/// Visual elevation tier for [UiMixSurface].
enum UiMixSurfaceVariant { card, elevated, recessed }

/// Option B `mix` Surface primitive driven by [UiMixTokens], [UiColors], and
/// [UiTokens] for card, elevated, recessed, and glassmorphic containers.
final class UiMixSurface extends StatelessWidget {
  const UiMixSurface({
    super.key,
    required this.child,
    this.variant = UiMixSurfaceVariant.card,
    this.padding,
    this.radius,
    this.shadows,
    this.color,
    this.borderColor,
    this.blurSigma,
    this.style,
    this.semanticsId,
  });

  final Widget child;
  final UiMixSurfaceVariant variant;
  final EdgeInsetsGeometry? padding;
  final double? radius;
  final List<BoxShadow>? shadows;
  final Color? color;
  final Color? borderColor;
  final double? blurSigma;
  final UiStyleData? style;
  final String? semanticsId;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);

    final styleShadows =
        shadows ??
        (style != null && style!.elevation != 'flat'
            ? UiStyleMapper.elevationShadows(colors, style!.elevation)
            : null);

    Widget surface;
    if (blurSigma != null ||
        colors.isGlass ||
        (styleShadows != null && styleShadows.isNotEmpty)) {
      final surfaceColor = UiMixTokens.surface.resolve(context);
      final recessedColor = UiMixTokens.recessed.resolve(context);
      final spaceMedium = UiMixTokens.spaceMedium.resolve(context);
      final cardRadius = UiMixTokens.cardRadius.resolve(context).x;
      final bg =
          color ??
          (style != null && style!.tone != 'default'
              ? UiStyleMapper.resolveToneBackground(colors, style!.tone)
              : switch (variant) {
                  UiMixSurfaceVariant.card => surfaceColor,
                  UiMixSurfaceVariant.elevated => surfaceColor,
                  UiMixSurfaceVariant.recessed => recessedColor,
                });
      final effectiveRadius = radius ?? style?.radius ?? cardRadius;
      final Color effectiveBorder =
          borderColor ??
          (colors.isGlass
              ? colors.specularBorder
              : UiMixTokens.hairline.resolve(context));
      surface = UiAcrylicSurface(
        radius: effectiveRadius,
        color: bg,
        borderColor: effectiveBorder,
        shadows: styleShadows ?? UiShadows.sm(colors),
        padding:
            padding ??
            EdgeInsets.symmetric(
              horizontal: style?.paddingX ?? spaceMedium,
              vertical: style?.paddingY ?? spaceMedium,
            ),
        sigmaOverride: blurSigma ?? colors.blurSigma,
        child: child,
      );
    } else {
      final bg =
          color ??
          (style != null && style!.tone != 'default'
              ? UiStyleMapper.resolveToneBackgroundToken(style!.tone)
              : switch (variant) {
                  UiMixSurfaceVariant.card => UiMixTokens.surface(),
                  UiMixSurfaceVariant.elevated => UiMixTokens.surface(),
                  UiMixSurfaceVariant.recessed => UiMixTokens.recessed(),
                });
      final customRadius = radius ?? style?.radius;
      final borderRadius = customRadius != null
          ? BorderRadiusGeometryMix.circular(customRadius)
          : BorderRadiusGeometryMix.all(UiMixTokens.cardRadius());
      final effectiveBorder = borderColor ?? UiMixTokens.hairline();
      final px = style?.paddingX ?? UiMixTokens.spaceMedium();
      final py = style?.paddingY ?? UiMixTokens.spaceMedium();

      var boxStyle = BoxStyler()
          .color(bg)
          .borderRadius(borderRadius)
          .border(
            BoxBorderMix.all(BorderSideMix.color(effectiveBorder).width(1.0)),
          );

      if (padding == null) {
        boxStyle = boxStyle.padding(
          EdgeInsetsGeometryMix.symmetric(horizontal: px, vertical: py),
        );
      }

      if (variant == UiMixSurfaceVariant.elevated) {
        boxStyle = boxStyle.shadow(
          BoxShadowMix(
            color: effectiveBorder.withValues(alpha: 0.35),
            blurRadius: 12,
            offset: const Offset(0, 4),
          ),
        );
      }

      surface = Box(
        style: boxStyle,
        child: padding != null
            ? Padding(padding: padding!, child: child)
            : child,
      );
    }

    if (semanticsId == null) return surface;
    return Semantics(identifier: semanticsId, child: surface);
  }
}

// --- src/primitives/expander.dart ---
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
    this.semanticsId,
  });

  final String header;
  final String subtitle;
  final bool isExpanded;
  final ValueChanged<bool> onExpandedChanged;
  final Widget body;
  final bool enabled;
  final UiStyleData style;
  final String? semanticsId;

  @override
  State<UiMixExpander> createState() => _UiMixExpanderState();
}

class _UiMixExpanderState extends State<UiMixExpander> {
  bool _hovered = false;
  bool _pressed = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final resolved = widget.style.resolve(
      hovered: _hovered && widget.enabled,
      pressed: _pressed && widget.enabled,
      focused: _focused && widget.enabled,
    );
    final duration = resolved.motion.duration;
    final curve = resolved.motion.curve;

    final card = Box(
      style: UiStyleMapper.boxStyler(
        context,
        widget.style,
        isHovered: _hovered,
        isPressed: _pressed,
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
                    padding: EdgeInsets.only(top: resolved.gap ?? 10),
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

// --- src/primitives/tabs.dart ---
class _StepTabIntent extends Intent {
  const _StepTabIntent(this.delta);
  final int delta;
}

/// Option B `naked_ui` (`NakedButton`) + `mix` Tabs control with roving
/// `â†` / `â†’` keyboard navigation, horizontal overflow scrolling, active tab
/// reveal, and disabled tab support.
final class UiMixTabs extends StatefulWidget {
  const UiMixTabs({
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
  State<UiMixTabs> createState() => _UiMixTabsState();
}

class _UiMixTabsState extends State<UiMixTabs> {
  List<GlobalKey> _keys = [];
  final List<FocusNode> _focusNodes = [];
  int? _focusedIndex;
  int? _hoveredIndex;

  List<String> get labels => widget.labels;
  int get index => widget.index;
  ValueChanged<int> get onChanged => widget.onChanged;
  Set<int> get disabled => widget.disabled;

  @override
  void initState() {
    super.initState();
    _syncNodes();
    _reveal(Duration.zero);
  }

  void _syncNodes() {
    while (_focusNodes.length < labels.length) {
      _focusNodes.add(FocusNode());
    }
    while (_focusNodes.length > labels.length) {
      _focusNodes.removeLast().dispose();
    }
    if (_keys.length != labels.length) {
      _keys = [for (var i = 0; i < labels.length; i++) GlobalKey()];
    }
  }

  @override
  void didUpdateWidget(UiMixTabs oldWidget) {
    super.didUpdateWidget(oldWidget);
    _syncNodes();
    if (oldWidget.index != widget.index ||
        oldWidget.labels.length != widget.labels.length) {
      final disableAnim = MediaQuery.disableAnimationsOf(context);
      _reveal(disableAnim ? Duration.zero : const Duration(milliseconds: 200));
    }
  }

  @override
  void dispose() {
    for (final node in _focusNodes) {
      node.dispose();
    }
    super.dispose();
  }

  void _stepFrom(int current, int delta) {
    if (labels.isEmpty) return;
    var candidate = current;
    for (var step = 0; step < labels.length; step++) {
      candidate = (candidate + delta + labels.length) % labels.length;
      if (!disabled.contains(candidate)) {
        _focusNodes[candidate].requestFocus();
        onChanged(candidate);
        return;
      }
    }
  }

  void _reveal(Duration duration) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || index < 0 || index >= _keys.length) return;
      final tab = _keys[index].currentContext;
      if (tab == null) return;
      Scrollable.ensureVisible(
        tab,
        alignment: 0.5,
        duration: duration,
        curve: UiTokens.of(context).ease,
      );
    });
  }

  @override
  Widget build(BuildContext context) {
    _syncNodes();
    final c = UiColors.of(context);
    final type = UiType.of(context);
    return Box(
      style: BoxStyler().border(
        BorderMix(bottom: BorderSideMix.color(c.hairline).width(1.0)),
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
    final focused = _focusedIndex == i;
    final hovered = _hoveredIndex == i;

    return Semantics(
      key: _keys[i],
      button: true,
      selected: active,
      enabled: !off,
      child: Shortcuts(
        shortcuts: const {
          SingleActivator(LogicalKeyboardKey.arrowLeft): _StepTabIntent(-1),
          SingleActivator(LogicalKeyboardKey.arrowRight): _StepTabIntent(1),
        },
        child: Actions(
          actions: {
            _StepTabIntent: CallbackAction<_StepTabIntent>(
              onInvoke: (intent) {
                if (!off) _stepFrom(i, intent.delta);
                return null;
              },
            ),
          },
          child: NakedButton(
            focusNode: _focusNodes[i],
            enabled: !off,
            onPressed: off ? null : () => onChanged(i),
            onHoverChange: (h) {
              if (!mounted) return;
              setState(() {
                if (h) {
                  _hoveredIndex = i;
                } else if (_hoveredIndex == i) {
                  _hoveredIndex = null;
                }
              });
            },
            onFocusChange: (f) {
              if (!mounted) return;
              setState(() {
                if (f) {
                  _focusedIndex = i;
                } else if (_focusedIndex == i) {
                  _focusedIndex = null;
                }
              });
            },
            child: Opacity(
              opacity: off ? 0.45 : 1.0,
              child: Box(
                key: ValueKey('ui_mix_tab_$i'),
                style: BoxStyler()
                    .color(
                      focused
                          ? c.accentTint
                          : hovered && !off
                          ? c.recessed.withValues(alpha: 0.6)
                          : Colors.transparent,
                    )
                    .padding(
                      EdgeInsetsGeometryMix.symmetric(
                        vertical: 9,
                        horizontal: 10,
                      ),
                    )
                    .border(
                      BorderMix(
                        bottom: BorderSideMix.color(
                          active ? c.accent : Colors.transparent,
                        ).width(2.0),
                      ),
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

// --- src/primitives/nav_bar.dart ---
final class UiNavBarItem {
  const UiNavBarItem({
    required this.id,
    required this.label,
    required this.icon,
    this.activeIcon,
    this.badgeCount,
    this.enabled = true,
  });

  final String id;
  final String label;
  final IconData icon;
  final IconData? activeIcon;
  final int? badgeCount;
  final bool enabled;
}

/// Mix-styled adaptive navigation bar:
/// - Bottom navigation bar on compact/mobile viewports (`width < compactBreakpoint`)
/// - Compact vertical navigation rail on medium/tablet+ viewports (`width >= compactBreakpoint`)
final class UiAdaptiveNavBar extends StatefulWidget {
  const UiAdaptiveNavBar({
    super.key,
    required this.items,
    required this.selectedIndex,
    required this.onSelected,
    this.compactBreakpoint = 640,
    this.railWidth = 72,
    this.barHeight = 60,
    this.leading,
    this.trailing,
    this.semanticsId,
  });

  final List<UiNavBarItem> items;
  final int selectedIndex;
  final ValueChanged<int> onSelected;
  final double compactBreakpoint;
  final double railWidth;
  final double barHeight;
  final Widget? leading;
  final Widget? trailing;
  final String? semanticsId;

  @override
  State<UiAdaptiveNavBar> createState() => _UiAdaptiveNavBarState();
}

class _UiAdaptiveNavBarState extends State<UiAdaptiveNavBar> {
  int? _hoveredIndex;
  int? _focusedIndex;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final type = UiType.of(context);
    final isCompact =
        MediaQuery.sizeOf(context).width < widget.compactBreakpoint;

    final nav = isCompact
        ? _buildBottomBar(c, tokens, type)
        : _buildRail(c, tokens, type);

    if (widget.semanticsId == null) return nav;
    return Semantics(identifier: widget.semanticsId, child: nav);
  }

  Widget _buildBottomBar(UiColors c, UiTokens tokens, UiTypeSet type) {
    return Box(
      key: const ValueKey('ui_adaptive_nav_bottom'),
      style: BoxStyler()
          .height(widget.barHeight)
          .color(c.surface)
          .padding(EdgeInsetsGeometryMix.symmetric(horizontal: tokens.p8))
          .border(BorderMix(top: BorderSideMix.color(c.hairline).width(1.0))),
      child: SafeArea(
        top: false,
        child: Row(
          mainAxisAlignment: MainAxisAlignment.spaceAround,
          children: [
            for (var i = 0; i < widget.items.length; i++)
              Expanded(
                child: _buildItem(
                  index: i,
                  item: widget.items[i],
                  c: c,
                  tokens: tokens,
                  type: type,
                  isRail: false,
                ),
              ),
          ],
        ),
      ),
    );
  }

  Widget _buildRail(UiColors c, UiTokens tokens, UiTypeSet type) {
    return Box(
      key: const ValueKey('ui_adaptive_nav_rail'),
      style: BoxStyler()
          .width(widget.railWidth)
          .color(c.surface)
          .padding(
            EdgeInsetsGeometryMix.symmetric(
              vertical: tokens.p12,
              horizontal: tokens.p6,
            ),
          )
          .border(BorderMix(right: BorderSideMix.color(c.hairline).width(1.0))),
      child: SafeArea(
        right: false,
        child: Column(
          children: [
            if (widget.leading != null) ...[
              widget.leading!,
              SizedBox(height: tokens.p12),
            ],
            Expanded(
              child: SingleChildScrollView(
                primary: false,
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    for (var i = 0; i < widget.items.length; i++) ...[
                      if (i > 0) SizedBox(height: tokens.p6),
                      _buildItem(
                        index: i,
                        item: widget.items[i],
                        c: c,
                        tokens: tokens,
                        type: type,
                        isRail: true,
                      ),
                    ],
                  ],
                ),
              ),
            ),
            if (widget.trailing != null) ...[
              SizedBox(height: tokens.p12),
              widget.trailing!,
            ],
          ],
        ),
      ),
    );
  }

  Widget _buildItem({
    required int index,
    required UiNavBarItem item,
    required UiColors c,
    required UiTokens tokens,
    required UiTypeSet type,
    required bool isRail,
  }) {
    final active = index == widget.selectedIndex;
    final enabled = item.enabled;
    final hovered = _hoveredIndex == index && enabled;
    final focused = _focusedIndex == index && enabled;
    final fg = active ? c.accent : c.muted;
    final bg = active
        ? c.accentTint
        : hovered
        ? c.recessed.withValues(alpha: 0.7)
        : Colors.transparent;

    final badge = item.badgeCount != null && item.badgeCount! > 0
        ? Positioned(
            right: -6,
            top: -4,
            child: Box(
              style: BoxStyler()
                  .color(c.error)
                  .padding(
                    EdgeInsetsGeometryMix.symmetric(horizontal: 4, vertical: 1),
                  )
                  .borderRadius(BorderRadiusGeometryMix.circular(tokens.pill)),
              child: Text(
                item.badgeCount! > 99 ? '99+' : '${item.badgeCount}',
                style: type.meta.copyWith(
                  fontSize: 9,
                  fontWeight: FontWeight.w700,
                  color: c.onAccent,
                  height: 1.1,
                ),
              ),
            ),
          )
        : null;

    return Semantics(
      button: true,
      selected: active,
      enabled: enabled,
      label: item.label,
      child: NakedButton(
        enabled: enabled,
        onPressed: enabled ? () => widget.onSelected(index) : null,
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
            key: ValueKey('ui_nav_item_${item.id}'),
            style: BoxStyler()
                .color(bg)
                .padding(
                  EdgeInsetsGeometryMix.symmetric(
                    horizontal: tokens.p6,
                    vertical: isRail ? tokens.p8 : tokens.p6,
                  ),
                )
                .borderRadius(
                  BorderRadiusGeometryMix.circular(tokens.controlRadius),
                )
                .border(
                  BoxBorderMix.all(
                    BorderSideMix.color(focused ? c.focus : Colors.transparent)
                        .width(1.5),
                  ),
                )
                .animate(
                  AnimationConfig.curve(
                    duration: tokens.fast,
                    curve: tokens.ease,
                  ),
                ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Stack(
                  clipBehavior: Clip.none,
                  children: [
                    Icon(
                      active ? (item.activeIcon ?? item.icon) : item.icon,
                      size: 18,
                      color: fg,
                    ),
                    ?badge,
                  ],
                ),
                SizedBox(height: tokens.p2),
                Text(
                  item.label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: type.meta.copyWith(
                    fontSize: 10.5,
                    fontWeight: active ? FontWeight.w600 : FontWeight.w500,
                    color: fg,
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

// --- src/primitives/sheet.dart ---
final class UiSheet extends StatelessWidget {
  const UiSheet({
    super.key,
    required this.child,
    this.title,
    this.subtitle,
    this.trailing,
    this.footer,
    this.showDragHandle = false,
    this.maxWidth = 520,
    this.padding,
    this.semanticsId,
  });

  final Widget child;
  final String? title;
  final String? subtitle;
  final Widget? trailing;
  final Widget? footer;
  final bool showDragHandle;
  final double maxWidth;
  final EdgeInsetsGeometry? padding;
  final String? semanticsId;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final type = UiType.of(context);
    final hasHeader =
        (title != null && title!.isNotEmpty) ||
        (subtitle != null && subtitle!.isNotEmpty) ||
        trailing != null;

    final content = ConstrainedBox(
      constraints: BoxConstraints(maxWidth: maxWidth),
      child: Box(
        style: BoxStyler()
            .color(c.elevated)
            .borderRadius(BorderRadiusGeometryMix.circular(tokens.modalRadius))
            .border(
              BoxBorderMix.all(BorderSideMix.color(c.hairline).width(1.0)),
            )
            .shadow(
              BoxShadowMix(
                color: c.ink.withValues(alpha: 0.16),
                blurRadius: 24,
                offset: const Offset(0, 8),
              ),
            ),
        child: Padding(
          padding: padding ?? EdgeInsets.all(tokens.p20),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (showDragHandle) ...[
                Center(
                  child: Box(
                    style: BoxStyler()
                        .width(36)
                        .height(4)
                        .color(c.borderStrong)
                        .borderRadius(
                          BorderRadiusGeometryMix.circular(tokens.pill),
                        ),
                  ),
                ),
                SizedBox(height: tokens.p12),
              ],
              if (hasHeader) ...[
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          if (title != null && title!.isNotEmpty)
                            Text(
                              title!,
                              style: type.body.copyWith(
                                fontSize: 16,
                                fontWeight: FontWeight.w600,
                                color: c.strong,
                              ),
                            ),
                          if (subtitle != null && subtitle!.isNotEmpty) ...[
                            SizedBox(height: tokens.p4),
                            Text(subtitle!, style: type.meta),
                          ],
                        ],
                      ),
                    ),
                    if (trailing != null) ...[
                      SizedBox(width: tokens.p12),
                      trailing!,
                    ],
                  ],
                ),
                SizedBox(height: tokens.p16),
              ],
              Flexible(child: child),
              if (footer != null) ...[SizedBox(height: tokens.p16), footer!],
            ],
          ),
        ),
      ),
    );

    if (semanticsId == null) return content;
    return Semantics(
      identifier: semanticsId,
      scopesRoute: true,
      explicitChildNodes: true,
      child: content,
    );
  }
}

/// Presents an adaptive modal sheet:
/// - Bottom sheet on compact/mobile viewports (`width < compactBreakpoint`)
/// - Centered dialog (or right-aligned side sheet when [sideSheet] is true) on desktop
Future<T?> showUiSheet<T>({
  required BuildContext context,
  required WidgetBuilder builder,
  bool barrierDismissible = true,
  double compactBreakpoint = 640,
  bool sideSheet = false,
}) {
  final width = MediaQuery.sizeOf(context).width;
  final isCompact = width < compactBreakpoint;

  if (isCompact) {
    return showModalBottomSheet<T>(
      context: context,
      isScrollControlled: true,
      isDismissible: barrierDismissible,
      enableDrag: barrierDismissible,
      backgroundColor: Colors.transparent,
      builder: (ctx) => SafeArea(
        top: false,
        child: Padding(
          padding: EdgeInsets.only(
            left: 8,
            right: 8,
            bottom: MediaQuery.viewInsetsOf(ctx).bottom + 8,
          ),
          child: builder(ctx),
        ),
      ),
    );
  }

  final tokens = UiTokens.of(context);
  return showGeneralDialog<T>(
    context: context,
    barrierDismissible: barrierDismissible,
    barrierLabel: MaterialLocalizations.of(context).modalBarrierDismissLabel,
    barrierColor: Colors.black.withValues(alpha: 0.45),
    transitionDuration: tokens.base,
    pageBuilder: (ctx, _, _) => SafeArea(
      child: Align(
        alignment: sideSheet ? Alignment.centerRight : Alignment.center,
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Material(type: MaterialType.transparency, child: builder(ctx)),
        ),
      ),
    ),
    transitionBuilder: (ctx, animation, _, child) {
      final curved = CurvedAnimation(parent: animation, curve: tokens.ease);
      if (sideSheet) {
        return SlideTransition(
          position: Tween<Offset>(
            begin: const Offset(0.08, 0),
            end: Offset.zero,
          ).animate(curved),
          child: FadeTransition(opacity: curved, child: child),
        );
      }
      return FadeTransition(
        opacity: curved,
        child: ScaleTransition(
          scale: Tween<double>(begin: 0.96, end: 1.0).animate(curved),
          child: child,
        ),
      );
    },
  );
}
