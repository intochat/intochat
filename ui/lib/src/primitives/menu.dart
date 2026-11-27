import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:mix/mix.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'button.dart';
import 'layout_primitives.dart';

/// Ghost square icon button (34x34, `radiusSmall` corners) with tooltip,
/// semantic label, hover fill, and keyboard focus halo.
final class UiGhostIconButton extends StatefulWidget {
  const UiGhostIconButton({
    super.key,
    required this.icon,
    required this.label,
    this.onPressed,
    this.focusNode,
  });

  final IconData icon;
  final String label;
  final VoidCallback? onPressed;
  final FocusNode? focusNode;

  @override
  State<UiGhostIconButton> createState() => _UiGhostIconButtonState();
}

class _UiGhostIconButtonState extends State<UiGhostIconButton> {
  bool _hover = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final enabled = widget.onPressed != null;
    return Opacity(
      opacity: enabled ? 1 : 0.45,
      child: Tooltip(
        message: widget.label,
        excludeFromSemantics: true,
        child: Semantics(
          button: true,
          enabled: enabled,
          label: widget.label,
          child: FocusableActionDetector(
            focusNode: widget.focusNode,
            enabled: enabled,
            mouseCursor: enabled
                ? SystemMouseCursors.click
                : SystemMouseCursors.basic,
            onShowHoverHighlight: (v) => setState(() => _hover = v),
            onShowFocusHighlight: (v) => setState(() => _focused = v),
            actions: {
              ActivateIntent: CallbackAction<ActivateIntent>(
                onInvoke: (_) => widget.onPressed?.call(),
              ),
            },
            child: GestureDetector(
              behavior: HitTestBehavior.opaque,
              onTap: widget.onPressed,
              child: Box(
                style: (() {
                  var s = BoxStyler()
                      .width(34)
                      .height(34)
                      .color(
                        _hover && enabled ? c.recessed : Colors.transparent,
                      )
                      .borderRadius(
                        BorderRadiusGeometryMix.circular(tokens.radiusSmall),
                      );
                  if (_focused) {
                    s = s.border(
                      BoxBorderMix.all(BorderSideMix.color(c.accent).width(2)),
                    );
                  }
                  return s;
                })(),
                child: Icon(widget.icon, size: uiIconSize, color: c.body),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Popover menu primitive: ghost trigger opening a 240 px surface menu.
/// Children are buttons (kind 1), references (kind 5), dividers (kind 12), and
/// section labels. Escape or tapping outside closes the popover and restores
/// focus to its trigger.
final class UiMenu extends StatefulWidget {
  const UiMenu({
    super.key,
    required this.node,
    required this.action,
    this.reference,
  });

  final Map node;
  final VoidCallback? Function(Map node) action;

  /// Builds a Reference (kind 5) item: a button neuron that keeps its own
  /// activation. Without it, references are not shown.
  final Widget Function(Map node)? reference;

  @override
  State<UiMenu> createState() => _UiMenuState();
}

class _UiMenuState extends State<UiMenu> {
  final _panel = FocusNode(debugLabel: 'ui menu');
  final _trigger = FocusNode(debugLabel: 'ui menu trigger');
  final _menu = MenuController();

  void _after(FocusNode node) =>
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted && node.context != null) node.requestFocus();
      });

  void _closeAfterFrame() =>
      WidgetsBinding.instance.addPostFrameCallback((_) => _menu.close());

  @override
  void dispose() {
    _panel.dispose();
    _trigger.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final node = widget.node;
    final action = widget.action;
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final type = UiType.of(context);
    final text = node['text'] as String? ?? '';
    final icon = node['icon'] as String?;
    final glyph = icon == null ? null : uiIcon(icon);
    final children = (node['children'] as List? ?? const [])
        .whereType<Map>()
        .take(512);
    final items = <Widget>[
      for (final child in children)
        switch (child['kind']) {
          1 => _UiMenuItem(node: child, onPressed: action(child)),
          5 when widget.reference != null => Focus(
            canRequestFocus: false,
            skipTraversal: true,
            onKeyEvent: (_, event) {
              if (event is KeyDownEvent &&
                  (event.logicalKey == LogicalKeyboardKey.enter ||
                      event.logicalKey == LogicalKeyboardKey.numpadEnter ||
                      event.logicalKey == LogicalKeyboardKey.space)) {
                _closeAfterFrame();
              }
              return KeyEventResult.ignored;
            },
            child: Listener(
              behavior: HitTestBehavior.translucent,
              onPointerUp: (_) => _closeAfterFrame(),
              child: Align(
                alignment: AlignmentDirectional.centerStart,
                child: widget.reference!(child),
              ),
            ),
          ),
          5 => const SizedBox.shrink(),
          12 => Container(
            height: 1,
            margin: const EdgeInsets.symmetric(vertical: 6),
            color: c.hairline,
          ),
          _ => Padding(
            padding: const EdgeInsets.fromLTRB(10, 8, 10, 4),
            child: Text(
              (child['text'] as String? ?? '').toUpperCase(),
              style: type.menuLabel,
            ),
          ),
        },
    ];
    return MenuAnchor(
      controller: _menu,
      clipBehavior: Clip.none,
      childFocusNode: _trigger,
      onOpen: () => _after(_panel),
      onClose: () => _after(_trigger),
      style: const MenuStyle(
        backgroundColor: WidgetStatePropertyAll(Colors.transparent),
        surfaceTintColor: WidgetStatePropertyAll(Colors.transparent),
        shadowColor: WidgetStatePropertyAll(Colors.transparent),
        elevation: WidgetStatePropertyAll(0),
        padding: WidgetStatePropertyAll(EdgeInsets.zero),
      ),
      menuChildren: [
        Focus(
          focusNode: _panel,
          child: Container(
            width: 240,
            padding: const EdgeInsets.all(6),
            decoration: BoxDecoration(
              color: c.surface,
              borderRadius: BorderRadius.circular(tokens.fieldRadius),
              border: Border.all(color: c.hairline),
              boxShadow: UiShadows.lg(uiKitShadowBase(context)),
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: items,
            ),
          ),
        ),
      ],
      builder: (context, controller, _) {
        void toggle() =>
            controller.isOpen ? controller.close() : controller.open();
        return glyph != null
            ? UiGhostIconButton(
                icon: glyph,
                label: text,
                onPressed: toggle,
                focusNode: _trigger,
              )
            : UiMixButton.preset(
                label: text,
                style: UiMixButtonStyle.ghost,
                onPressed: toggle,
                focusNode: _trigger,
              );
      },
    );
  }
}

class _UiMenuItem extends StatelessWidget {
  const _UiMenuItem({required this.node, required this.onPressed});

  final Map node;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final icon = node['icon'] as String?;
    final glyph = icon == null ? null : uiIcon(icon);
    bool lit(Set<WidgetState> s) =>
        s.contains(WidgetState.hovered) || s.contains(WidgetState.focused);
    return MenuItemButton(
      onPressed: onPressed,
      style: ButtonStyle(
        minimumSize: const WidgetStatePropertyAll(Size(0, 36)),
        maximumSize: const WidgetStatePropertyAll(Size(double.infinity, 36)),
        padding: const WidgetStatePropertyAll(
          EdgeInsets.symmetric(horizontal: 10),
        ),
        shape: WidgetStatePropertyAll(
          RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(tokens.radiusSmall),
          ),
        ),
        side: WidgetStateProperty.resolveWith(
          (s) => s.contains(WidgetState.focused)
              ? BorderSide(color: c.focus, width: 3)
              : BorderSide.none,
        ),
        backgroundColor: WidgetStateProperty.resolveWith(
          (s) => lit(s) ? c.menuHover : Colors.transparent,
        ),
        foregroundColor: WidgetStateProperty.resolveWith(
          (s) => lit(s) ? c.strong : c.body,
        ),
        overlayColor: const WidgetStatePropertyAll(Colors.transparent),
        textStyle: WidgetStatePropertyAll(UiType.of(context).menuItem),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (glyph != null) ...[
            Icon(glyph, size: uiIconSize, color: c.muted),
            const SizedBox(width: 10),
          ],
          Flexible(
            child: Text(
              node['text'] as String? ?? '',
              overflow: TextOverflow.ellipsis,
            ),
          ),
        ],
      ),
    );
  }
}
