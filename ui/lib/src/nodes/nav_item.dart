import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import 'node_context.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

Widget renderNavItem(NavItemNode node, NodeContext ctx) =>
    _NavItemPill(key: ValueKey('nav:${node.key}'), node: node, ctx: ctx);

class _NavItemPill extends StatefulWidget {
  const _NavItemPill({super.key, required this.node, required this.ctx});

  final NavItemNode node;
  final NodeContext ctx;

  @override
  State<_NavItemPill> createState() => _NavItemPillState();
}

class _NavItemPillState extends State<_NavItemPill> {
  bool _hover = false;
  bool _focused = false;

  void _activate() {
    if (widget.ctx.interactive) {
      widget.ctx.activate(widget.node.key, context: context);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final disableAnimations = MediaQuery.disableAnimationsOf(context);
    final duration = disableAnimations ? Duration.zero : tokens.fast;
    final node = widget.node;
    final selected = node.selected;
    final interactive = widget.ctx.interactive;
    final bg = selected
        ? c.accentTint
        : (_hover && interactive ? c.menuHover : Colors.transparent);
    final fg = selected ? c.accent : (_hover ? c.strong : c.body);
    final iconData =
        (node.icon.isNotEmpty ? uiIcon(node.icon) : null) ??
        LucideIcons.circleDot;

    return Semantics(
      button: true,
      selected: selected,
      enabled: interactive,
      label: node.label,
      child: FocusableActionDetector(
        enabled: interactive,
        mouseCursor: interactive ? SystemMouseCursors.click : MouseCursor.defer,
        onShowFocusHighlight: (f) => setState(() => _focused = f),
        onShowHoverHighlight: (h) => setState(() => _hover = h),
        actions: {
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) {
              _activate();
              return null;
            },
          ),
        },
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: interactive ? _activate : null,
          child: Stack(
            clipBehavior: Clip.none,
            children: [
              if (_focused)
                Positioned(
                  left: -2,
                  top: -2,
                  right: -2,
                  bottom: -2,
                  child: IgnorePointer(
                    child: DecoratedBox(
                      key: ValueKey('nav_ring:${node.key}'),
                      decoration: BoxDecoration(
                        borderRadius: BorderRadius.circular(tokens.sm + 2),
                        border: Border.all(color: c.focus, width: 3),
                      ),
                    ),
                  ),
                ),
              AnimatedContainer(
                duration: duration,
                curve: tokens.ease,
                height: tokens.rowHeight,
                decoration: BoxDecoration(
                  color: bg,
                  borderRadius: BorderRadius.circular(tokens.sm),
                  border: _focused ? Border.all(color: c.accent) : null,
                ),
                child: Row(
                  children: [
                    AnimatedContainer(
                      duration: duration,
                      curve: tokens.ease,
                      width: 3,
                      height: selected ? 18 : 0,
                      decoration: BoxDecoration(
                        color: selected ? c.accent : Colors.transparent,
                        borderRadius: BorderRadius.circular(tokens.pill),
                      ),
                    ),
                    const SizedBox(width: 7),
                    Icon(
                      iconData,
                      size: 16,
                      color: selected ? c.accent : c.muted,
                    ),
                    SizedBox(width: tokens.p6),
                    Expanded(
                      child: Text(
                        node.label,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: UiType.textStyle(
                          13,
                          weight: selected ? FontWeight.w600 : FontWeight.w500,
                          color: fg,
                        ),
                      ),
                    ),
                    if (node.badge case final badge? when badge.isNotEmpty) ...[
                      const SizedBox(width: 6),
                      Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 6,
                          vertical: 2,
                        ),
                        decoration: BoxDecoration(
                          color: selected
                              ? c.accent.withValues(alpha: 0.14)
                              : c.recessed,
                          borderRadius: BorderRadius.circular(tokens.xs),
                        ),
                        child: Text(
                          badge,
                          style: UiType.tabularStyle(
                            11,
                            weight: FontWeight.w600,
                            color: selected ? c.accent : c.muted,
                          ),
                        ),
                      ),
                    ],
                    const SizedBox(width: 10),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
