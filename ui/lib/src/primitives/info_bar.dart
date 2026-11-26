import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';
import 'package:mix/mix.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'icon_button.dart';

/// Backward-compatible alias for [UiMixInfoBar].
typedef UiInfoBar = UiMixInfoBar;

/// Option B `mix`-styled severity-tinted message bar (`DESIGN.md` §12.4).
/// Severity is `info|success|warning|error`; anything else renders as `info`.
final class UiMixInfoBar extends StatelessWidget {
  const UiMixInfoBar({
    super.key,
    required this.severity,
    required this.title,
    required this.body,
    this.trailing,
    this.onDismiss,
  });

  final String severity;
  final String title;
  final String body;
  final Widget? trailing;
  final VoidCallback? onDismiss;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final type = UiType.of(context);
    final (background, foreground, border, icon) = switch (severity) {
      'success' => (
        c.doneBg,
        c.done,
        c.done.withValues(alpha: 0.32),
        LucideIcons.check,
      ),
      'warning' => (
        c.workingBg,
        c.working,
        c.working.withValues(alpha: 0.32),
        LucideIcons.triangleAlert,
      ),
      'error' => (
        c.errorBg,
        c.error,
        c.error.withValues(alpha: 0.35),
        LucideIcons.triangleAlert,
      ),
      _ => (
        c.accentTint,
        c.accent,
        c.accent.withValues(alpha: 0.28),
        LucideIcons.info,
      ),
    };

    return Semantics(
      container: true,
      liveRegion: true,
      child: Box(
        key: const Key('ui_info_bar'),
        style: BoxStyler()
            .color(background)
            .borderRadius(BorderRadiusGeometryMix.circular(tokens.card))
            .border(BoxBorderMix.all(BorderSideMix.color(border).width(1.0)))
            .padding(
              EdgeInsetsGeometryMix.fromLTRB(
                14,
                10,
                trailing != null || onDismiss != null ? 10 : 14,
                10,
              ),
            ),
        child: LayoutBuilder(
          builder: (context, constraints) {
            final stackTrailing =
                trailing != null &&
                constraints.hasBoundedWidth &&
                constraints.maxWidth < 360;
            final content = Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Padding(
                  padding: const EdgeInsets.only(top: 2),
                  child: Icon(icon, size: 16, color: foreground),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      if (title.isNotEmpty)
                        Text(
                          title,
                          style: type.cardTitle.copyWith(fontSize: 13.5),
                        ),
                      if (title.isNotEmpty && body.isNotEmpty)
                        const SizedBox(height: 2),
                      if (body.isNotEmpty) Text(body, style: type.body),
                    ],
                  ),
                ),
                if (!stackTrailing && trailing != null) ...[
                  const SizedBox(width: 12),
                  trailing!,
                ],
                if (onDismiss != null) ...[
                  const SizedBox(width: 4),
                  UiMixIconButton(
                    icon: LucideIcons.x,
                    iconSize: 16,
                    iconColor: c.muted,
                    tooltip: 'Dismiss',
                    onPressed: onDismiss,
                  ),
                ],
              ],
            );
            if (!stackTrailing) return content;
            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                content,
                const SizedBox(height: 8),
                Padding(
                  padding: const EdgeInsets.only(left: 26),
                  child: trailing!,
                ),
              ],
            );
          },
        ),
      ),
    );
  }
}
