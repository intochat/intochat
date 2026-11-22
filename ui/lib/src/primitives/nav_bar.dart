import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

/// Single destination item for [UiAdaptiveNavBar].
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
    final isCompact = MediaQuery.sizeOf(context).width < widget.compactBreakpoint;

    final nav = isCompact
        ? _buildBottomBar(c, tokens, type)
        : _buildRail(c, tokens, type);

    if (widget.semanticsId == null) return nav;
    return Semantics(
      identifier: widget.semanticsId,
      child: nav,
    );
  }

  Widget _buildBottomBar(UiColors c, UiTokens tokens, UiTypeSet type) {
    return Box(
      key: const ValueKey('ui_adaptive_nav_bottom'),
      style: BoxStyler()
          .height(widget.barHeight)
          .color(c.surface)
          .padding(EdgeInsetsGeometryMix.symmetric(horizontal: tokens.p8))
          .border(
            BorderMix(top: BorderSideMix.color(c.hairline).width(1.0)),
          ),
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
          .border(
            BorderMix(right: BorderSideMix.color(c.hairline).width(1.0)),
          ),
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
                    BorderSideMix.color(
                      focused ? c.focus : Colors.transparent,
                    ).width(1.5),
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
