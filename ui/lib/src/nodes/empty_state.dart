import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/app_tile.dart';
import '../design/keys.dart';
import '../theme/ui_tokens.dart';
import '../theme/ui_icon.dart';
import '../theme/wire_node_values.dart';
import 'button.dart';
import 'node_context.dart';

Widget renderEmptyState(EmptyStateNode node, NodeContext ctx) =>
    EmptyStateView(node: node, ctx: ctx);

/// Centered empty session or view state with title, optional body,
/// and keyboard-navigable suggestion cards.
class EmptyStateView extends StatelessWidget {
  const EmptyStateView({super.key, required this.node, required this.ctx});

  final EmptyStateNode node;
  final NodeContext ctx;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final body = node.body;
    final suggestions = node.suggestions ?? const <ButtonNode>[];
    final appId = inferAppIdFromViewPath(ctx.view);
    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(24, 24, 24, 32),
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 620),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              IcAppIcon(appId: appId, size: 48),
              const SizedBox(height: 18),
              Text(
                node.title,
                textAlign: TextAlign.center,
                style: IcType.heading(
                  26,
                  weight: FontWeight.w800,
                  color: c.textStrong,
                ),
              ),
              if (body != null && body.trim().isNotEmpty) ...[
                const SizedBox(height: 8),
                Text(
                  body,
                  textAlign: TextAlign.center,
                  style: IcType.text(14, color: c.textMuted, height: 1.45),
                ),
              ],
              if (suggestions.isNotEmpty) ...[
                const SizedBox(height: 24),
                PromptKeys(
                  child: LayoutBuilder(
                    builder: (context, box) {
                      final hasFiniteWidth = box.maxWidth.isFinite;
                      final cardWidth = !hasFiniteWidth
                          ? 280.0
                          : box.maxWidth < 460
                          ? box.maxWidth
                          : (box.maxWidth - 10) / 2;
                      return Wrap(
                        spacing: 10,
                        runSpacing: 10,
                        children: [
                          for (final btn in suggestions)
                            _SuggestionCard(
                              width: cardWidth,
                              button: btn,
                              onTap: btn.enabled && ctx.interactive
                                  ? () => pressCommand(
                                      btn.key,
                                      btn.command,
                                      ctx,
                                      context,
                                    )
                                  : null,
                            ),
                        ],
                      );
                    },
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class _SuggestionCard extends StatefulWidget {
  const _SuggestionCard({
    required this.width,
    required this.button,
    required this.onTap,
  });

  final double width;
  final ButtonNode button;
  final VoidCallback? onTap;

  @override
  State<_SuggestionCard> createState() => _SuggestionCardState();
}

class _SuggestionCardState extends State<_SuggestionCard> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final disabled = widget.onTap == null;
    final glyph =
        (widget.button.icon != null ? uiIcon(widget.button.icon!) : null) ??
        LucideIcons.sparkles;
    return Stop(
      onActivate: widget.onTap,
      builder: (context, v) => Opacity(
        opacity: disabled ? 0.5 : 1,
        child: MouseRegion(
          cursor: disabled ? MouseCursor.defer : SystemMouseCursors.click,
          onEnter: (_) => setState(() => _hover = true),
          onExit: (_) => setState(() => _hover = false),
          child: GestureDetector(
            onTap: widget.onTap,
            child: AnimatedContainer(
              duration: IcMotion.fast,
              width: widget.width,
              constraints: const BoxConstraints(minHeight: 62),
              transform: Matrix4.translationValues(
                0,
                _hover && !disabled ? -1 : 0,
                0,
              ),
              padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(
                color: v.selected ? c.accentTint : c.surfaceCard,
                borderRadius: BorderRadius.circular(IcRadius.card),
                border: Border.all(
                  color: v.selected
                      ? c.accent
                      : (_hover && !disabled
                            ? c.borderInput
                            : c.borderHairline),
                  width: v.selected ? 1.5 : 1,
                ),
                boxShadow: [IcShadow.sm(c).first],
              ),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(glyph, size: 17, color: c.textMuted),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      widget.button.label,
                      style: IcType.text(14, color: c.textBody, height: 1.4),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
