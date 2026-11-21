import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../primitives/toast.dart';
import '../../design/controls.dart' show WorkingSpinner;
import '../../nodes/node_common.dart';
import '../../theme/brand_visuals.dart';
import '../../theme/ui_colors.dart';
import '../../theme/ui_icon.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';

/// Badge / TagPill value node unified with `TagPill` (`controls.dart`) and
/// `DESIGN.md` Â§2.5 & Â§11.8:
/// 22px height, 6px `xs` radius, `11.5px w600 tnum` text + `12px` status icon
/// (`check`, spinning `loaderCircle`, `triangleAlert`, `hand`, `clock`).
final class ViewBadge extends StatelessWidget {
  const ViewBadge({
    super.key,
    required this.text,
    required this.tone,
    this.icon,
    this.statusBadge = false,
  });

  final String text;
  final Object? tone;
  final String? icon;

  /// True when rendered by `BadgeNode` (status pill), ensuring `accent` /
  /// `needs-you` and `idle` / `neutral` carry their `DESIGN.md` Â§2.5 status icons.
  final bool statusBadge;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final rawTone = tone is String ? (tone as String).trim().toLowerCase() : '';
    final isNeedsYou =
        rawTone == 'needs-you' ||
        rawTone == 'needs_you' ||
        rawTone == 'needsyou';
    final isIdle =
        rawTone == 'idle' || rawTone == 'waiting' || rawTone == 'off';
    final isAccent =
        isNeedsYou ||
        tone == UiNodeTone.accent ||
        (tone is String && nodeToneOf(rawTone) == UiNodeTone.accent);
    final resolvedTone = switch (tone) {
      final UiNodeTone nt => switch (nt) {
        UiNodeTone.done => UiTone.done,
        UiNodeTone.working => UiTone.working,
        UiNodeTone.error => UiTone.error,
        UiNodeTone.neutral || UiNodeTone.accent => UiTone.neutral,
      },
      final String s => toneOf(s),
      _ => UiTone.fromWire(tone),
    };

    final (
      Color fill,
      Color ink,
      IconData? defaultIcon,
      bool spinDefault,
    ) = isAccent
        ? (
            c.needsYouBg,
            c.needsYou,
            (statusBadge || isNeedsYou) ? LucideIcons.hand : null,
            false,
          )
        : switch (resolvedTone) {
            UiTone.done => (c.doneBg, c.done, LucideIcons.check, false),
            UiTone.working => (
              c.workingBg,
              c.working,
              LucideIcons.loaderCircle,
              true,
            ),
            UiTone.error => (
              c.errorBg,
              c.error,
              LucideIcons.triangleAlert,
              false,
            ),
            UiTone.neutral => (
              c.surfaceSunk,
              (statusBadge || isIdle) ? c.idle : c.textMuted,
              (statusBadge || isIdle) ? LucideIcons.clock : null,
              false,
            ),
          };

    final brandSpec = (icon != null && icon!.contains(':'))
        ? BrandVisuals.tryForIconKey(icon)
        : null;
    final customIcon = (brandSpec == null && icon != null && icon!.isNotEmpty)
        ? uiIcon(icon!)
        : null;
    final glyph = brandSpec == null ? (customIcon ?? defaultIcon) : null;
    final useSpinner =
        brandSpec == null &&
        ((customIcon == null && spinDefault) ||
            icon == 'loader-circle' ||
            icon == 'loader');

    return Align(
      alignment: AlignmentDirectional.centerStart,
      widthFactor: 1,
      heightFactor: 1,
      child: Container(
        height: tokens.tagPillHeight,
        padding: const EdgeInsets.symmetric(horizontal: 7),
        decoration: BoxDecoration(
          color: fill,
          borderRadius: BorderRadius.circular(tokens.xs),
          border: Border.all(color: ink.withValues(alpha: 0.25)),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (brandSpec != null) ...[
              IcBrandIcon(spec: brandSpec, size: 12),
              const SizedBox(width: 4),
            ] else if (useSpinner) ...[
              WorkingSpinner(size: 12, color: ink),
              const SizedBox(width: 4),
            ] else if (glyph != null) ...[
              Icon(glyph, size: 12, color: ink),
              const SizedBox(width: 4),
            ],
            Flexible(
              child: Text(
                text,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: UiType.textStyle(
                  11.5,
                  weight: FontWeight.w600,
                  color: ink,
                  tnum: true,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
