import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';
import 'package:mix/mix.dart';

import '../design/controls.dart' show WorkingSpinner;
import '../nodes/node_common.dart';
import '../theme/brand_visuals.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'toast.dart';

/// Semantic tone for [UiBadge].
enum UiBadgeTone {
  neutral,
  accent,
  success,
  warning,
  danger;

  /// Parses wire or node tone representations (`UiBadgeTone`, `UiNodeTone`,
  /// integer tone indices `0..3`, or string names such as `done`, `working`,
  /// `error`, `needs-you`, `idle`, `accent`, `success`, `warning`, `danger`).
  static UiBadgeTone fromWire(Object? raw) {
    if (raw is UiBadgeTone) return raw;
    if (raw is UiNodeTone) {
      return switch (raw) {
        UiNodeTone.done => UiBadgeTone.success,
        UiNodeTone.working => UiBadgeTone.warning,
        UiNodeTone.error => UiBadgeTone.danger,
        UiNodeTone.accent => UiBadgeTone.accent,
        UiNodeTone.neutral => UiBadgeTone.neutral,
      };
    }
    if (raw is String) {
      final norm = raw.trim().toLowerCase();
      if (norm == 'needs-you' || norm == 'needs_you' || norm == 'needsyou') {
        return UiBadgeTone.accent;
      }
      if (norm == 'idle' || norm == 'waiting' || norm == 'off') {
        return UiBadgeTone.neutral;
      }
      return fromWire(nodeToneOf(norm));
    }
    return switch (UiTone.fromWire(raw)) {
      UiTone.done => UiBadgeTone.success,
      UiTone.working => UiBadgeTone.warning,
      UiTone.error => UiBadgeTone.danger,
      UiTone.neutral => UiBadgeTone.neutral,
    };
  }
}

/// Mix-styled status and metadata badge unified across primitives and wire
/// nodes (`DESIGN.md` §2.5 & §11.8).
final class UiBadge extends StatelessWidget {
  const UiBadge({
    super.key,
    required this.label,
    this.tone = UiBadgeTone.neutral,
    this.icon,
    this.iconKey,
    this.dot = false,
    this.statusBadge = false,
    this.showToneIcon = false,
    this.semanticsId,
  });

  /// Creates a [UiBadge] from wire/node values (supporting string/int/enum
  /// tones, `BrandVisuals` `:` keys, `uiIcon` keys, and `DESIGN.md` §2.5 status
  /// icon pairing).
  factory UiBadge.fromWire({
    Key? key,
    required String label,
    Object? tone,
    String? icon,
    bool statusBadge = false,
    String? semanticsId,
  }) {
    final rawTone = tone is String ? tone.trim().toLowerCase() : '';
    final forceStatus =
        statusBadge ||
        rawTone == 'needs-you' ||
        rawTone == 'needs_you' ||
        rawTone == 'needsyou' ||
        rawTone == 'idle' ||
        rawTone == 'waiting' ||
        rawTone == 'off';
    return UiBadge(
      key: key,
      label: label,
      tone: UiBadgeTone.fromWire(tone),
      iconKey: icon,
      statusBadge: forceStatus,
      showToneIcon: true,
      semanticsId: semanticsId,
    );
  }

  final String label;
  final UiBadgeTone tone;
  final IconData? icon;
  final String? iconKey;
  final bool dot;

  /// True for status pills (`BadgeNode` / `needs-you` / `idle`), pairing
  /// `accent` with `hand` and `neutral` with `clock` per `DESIGN.md` §2.5.
  final bool statusBadge;

  /// When true, renders the default semantic icon for `success`, `warning`,
  /// and `danger` tones when no explicit [icon] or [iconKey] is provided.
  final bool showToneIcon;
  final String? semanticsId;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);

    final (
      Color bg,
      Color fg,
      IconData? defaultIcon,
      bool spinDefault,
    ) = switch (tone) {
      UiBadgeTone.accent => (
        c.needsYouBg,
        c.needsYou,
        statusBadge ? LucideIcons.hand : null,
        false,
      ),
      UiBadgeTone.success => (
        c.doneBg,
        c.done,
        (statusBadge || showToneIcon) ? LucideIcons.check : null,
        false,
      ),
      UiBadgeTone.warning => (
        c.workingBg,
        c.working,
        (statusBadge || showToneIcon) ? LucideIcons.loaderCircle : null,
        statusBadge || showToneIcon,
      ),
      UiBadgeTone.danger => (
        c.errorBg,
        c.error,
        (statusBadge || showToneIcon) ? LucideIcons.triangleAlert : null,
        false,
      ),
      UiBadgeTone.neutral => (
        c.recessed,
        statusBadge ? c.idle : c.muted,
        statusBadge ? LucideIcons.clock : null,
        false,
      ),
    };

    final brandSpec = (iconKey != null && iconKey!.contains(':'))
        ? BrandVisuals.tryForIconKey(iconKey)
        : null;
    final customIcon =
        icon ??
        ((brandSpec == null && iconKey != null && iconKey!.isNotEmpty)
            ? uiIcon(iconKey!)
            : null);
    final glyph = brandSpec == null ? (customIcon ?? defaultIcon) : null;
    final useSpinner =
        !dot &&
        brandSpec == null &&
        ((customIcon == null && spinDefault) ||
            iconKey == 'loader-circle' ||
            iconKey == 'loader');

    final badge = Align(
      alignment: AlignmentDirectional.centerStart,
      widthFactor: 1,
      heightFactor: 1,
      child: Box(
        style: BoxStyler()
            .height(tokens.tagPillHeight)
            .color(bg)
            .padding(EdgeInsetsGeometryMix.symmetric(horizontal: 7))
            .borderRadius(BorderRadiusGeometryMix.circular(tokens.xs))
            .border(
              BoxBorderMix.all(
                BorderSideMix.color(fg.withValues(alpha: 0.25)).width(1.0),
              ),
            ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (dot) ...[
              Box(
                style: BoxStyler()
                    .width(6)
                    .height(6)
                    .color(fg)
                    .borderRadius(
                      BorderRadiusGeometryMix.circular(tokens.pill),
                    ),
              ),
              SizedBox(width: tokens.p6),
            ] else if (brandSpec != null) ...[
              IcBrandIcon(spec: brandSpec, size: 12),
              const SizedBox(width: 4),
            ] else if (useSpinner) ...[
              WorkingSpinner(size: 12, color: fg),
              const SizedBox(width: 4),
            ] else if (glyph != null) ...[
              Icon(glyph, size: 12, color: fg),
              const SizedBox(width: 4),
            ],
            Flexible(
              child: Text(
                label,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: UiType.textStyle(
                  11.5,
                  weight: FontWeight.w600,
                  color: fg,
                  tnum: true,
                ),
              ),
            ),
          ],
        ),
      ),
    );

    if (semanticsId == null) return badge;
    return Semantics(identifier: semanticsId, label: label, child: badge);
  }
}
