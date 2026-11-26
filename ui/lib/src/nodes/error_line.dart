import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/controls.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'button.dart';
import 'node_context.dart';

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
              Btn(
                label: action.label,
                small: true,
                onTap: action.enabled && ctx.interactive
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
