import 'package:flutter/material.dart';
import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/app_tile.dart';
import '../design/keys.dart';
import '../primitives/button.dart';
import '../primitives/display_primitives.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import '../theme/wire_node_values.dart';
import 'action_nodes.dart';
import 'node_common.dart';
import 'node_context.dart';

// --- src/nodes/toast.dart ---
Widget renderToast(ToastNode node, NodeContext ctx) =>
    KitToastBody(message: node.message);

// --- src/nodes/info_bar.dart ---
Widget renderInfoBar(InfoBarNode node, NodeContext ctx) => Builder(
  builder: (context) => Semantics(
    label: node.automationName.isEmpty ? null : node.automationName,
    container: true,
    child: UiInfoBar(
      severity: switch (nodeToneOf(node.tone)) {
        UiNodeTone.done => 'success',
        UiNodeTone.working => 'warning',
        UiNodeTone.error => 'error',
        UiNodeTone.accent || UiNodeTone.neutral => 'info',
      },
      title: node.title,
      body: node.message,
      trailing: node.action == null ? null : renderButton(node.action!, ctx),
      onDismiss: node.close == null || !ctx.interactive
          ? null
          : () => pressCommand(node.key, node.close, ctx, context),
    ),
  ),
);

// --- src/nodes/progress.dart ---
Widget renderProgress(ProgressNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final c = UiColors.of(context);
    final determinate =
        !node.isIndeterminate && !node.value.isNaN && node.value >= 0;
    final normalized = determinate
        ? (node.value > 1 ? node.value / 100 : node.value).clamp(0.0, 1.0)
        : 0.0;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        UiProgress(
          determinate: determinate,
          value: normalized,
          label: node.label,
          error: node.showError,
          ring: node.ring,
        ),
        if (node.detail case final detail? when detail.isNotEmpty) ...[
          const SizedBox(height: 4),
          Text(
            detail,
            style: UiType.tabularStyle(
              12.5,
              color: node.showError ? c.error : c.muted,
            ),
          ),
        ],
      ],
    );
  },
);

// --- src/nodes/error_line.dart ---
Widget renderErrorLine(ErrorLineNode node, NodeContext ctx) =>
    ErrorLineView(node: node, ctx: ctx);

/// Inline session or app error line: says what happened, optional detail,
/// and the single next action button.
class ErrorLineView extends StatelessWidget {
  const ErrorLineView({
    super.key,
    required this.node,
    required this.ctx,
    this.onAction,
  });

  final ErrorLineNode node;
  final NodeContext ctx;

  /// Runs the action instead of sending its command on [ctx]'s view: an action
  /// a parent or the host decided, whose node is not in this view.
  final VoidCallback? onAction;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final action = node.action;
    final detail = node.detail;
    return ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 680),
      child: Container(
        padding: const EdgeInsets.fromLTRB(14, 10, 10, 10),
        decoration: BoxDecoration(
          color: c.errorBg,
          borderRadius: BorderRadius.circular(UiTokens.of(context).card),
          border: Border.all(color: c.error.withValues(alpha: 0.35)),
        ),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.center,
          children: [
            SizedBox(
              width: 18,
              child: Icon(LucideIcons.circleAlert, size: 16, color: c.error),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: Padding(
                padding: const EdgeInsets.symmetric(vertical: 4),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      node.what,
                      style: UiType.textStyle(
                        14,
                        weight: FontWeight.w500,
                        color: c.textStrong,
                        height: 1.4,
                      ),
                    ),
                    if (detail != null && detail.trim().isNotEmpty) ...[
                      const SizedBox(height: 2),
                      Text(
                        detail,
                        style: UiType.textStyle(
                          12.5,
                          color: c.textMuted,
                          height: 1.35,
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            ),
            if (action != null) ...[
              const SizedBox(width: 12),
              UiMixButton.preset(
                label: action.label,
                style: UiMixButtonStyle.secondary,
                compact: true,
                onPressed: action.enabled && ctx.interactive
                    ? onAction ??
                          () => pressCommand(
                            action.key,
                            action.command,
                            ctx,
                            context,
                          )
                    : null,
              ),
            ],
          ],
        ),
      ),
    );
  }
}

// --- src/nodes/empty_state.dart ---
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
    final c = UiColors.of(context);
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
                style: UiType.displayStyle(
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
                  style: UiType.textStyle(14, color: c.textMuted, height: 1.45),
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
    final c = UiColors.of(context);
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
              duration: UiTokens.of(context).fast,
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
                borderRadius: BorderRadius.circular(UiTokens.of(context).card),
                border: Border.all(
                  color: v.selected
                      ? c.accent
                      : (_hover && !disabled
                            ? c.borderInput
                            : c.borderHairline),
                  width: v.selected ? 1.5 : 1,
                ),
                boxShadow: [UiShadows.sm(c).first],
              ),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(glyph, size: 17, color: c.textMuted),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      widget.button.label,
                      style: UiType.textStyle(
                        14,
                        color: c.textBody,
                        height: 1.4,
                      ),
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
