import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'button.dart';
import 'node_context.dart';
import '../design/app_tile.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

Widget renderAppTile(AppTileNode node, NodeContext ctx) =>
    _AppTileCard(key: ValueKey('appTile:${node.key}'), node: node, ctx: ctx);

class _AppTileCard extends StatefulWidget {
  const _AppTileCard({super.key, required this.node, required this.ctx});

  final AppTileNode node;
  final NodeContext ctx;

  @override
  State<_AppTileCard> createState() => _AppTileCardState();
}

class _AppTileCardState extends State<_AppTileCard> {
  bool _hover = false;
  bool _focused = false;

  void _activate() {
    if (widget.ctx.interactive) {
      pressCommand(widget.node.key, widget.node.command, widget.ctx, context);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final disableAnimations = MediaQuery.disableAnimationsOf(context);
    final duration = disableAnimations ? Duration.zero : tokens.fast;
    final node = widget.node;
    final interactive = widget.ctx.interactive;
    final iconData = (node.icon != null && node.icon!.isNotEmpty
        ? uiIcon(node.icon!)
        : null);

    return Semantics(
      button: true,
      enabled: interactive,
      excludeSemantics: true,
      label: controlNameOf(node.automationName, node.title, node.key),
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
          child: AnimatedContainer(
            duration: duration,
            curve: tokens.ease,
            padding: EdgeInsets.symmetric(
              horizontal: tokens.p12,
              vertical: tokens.p8,
            ),
            decoration: BoxDecoration(
              color: _hover && interactive ? c.menuHover : c.surface,
              borderRadius: BorderRadius.circular(tokens.button),
              border: Border.all(
                color: _focused ? c.focus : c.hairline,
                width: _focused ? 2 : 1,
              ),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                IcAppIcon(appId: node.appId, icon: iconData, size: 32),
                SizedBox(width: tokens.p10),
                Flexible(
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        node.title,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: UiType.textStyle(
                          13,
                          weight: FontWeight.w600,
                          color: c.strong,
                        ),
                      ),
                      if (node.category case final category?
                          when category.isNotEmpty)
                        Text(
                          category,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: UiType.textStyle(11, color: c.muted),
                        ),
                    ],
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
