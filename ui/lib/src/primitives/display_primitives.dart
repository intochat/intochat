import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../nodes/node_common.dart';
import '../theme/brand_visuals.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'icon_button.dart';
import 'layout_primitives.dart';
import 'style.dart';

// --- src/primitives/badge.dart ---
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

// --- src/primitives/chip.dart ---
final class UiMixChip extends StatefulWidget {
  const UiMixChip({
    super.key,
    required this.label,
    required this.selected,
    required this.onSelected,
    this.icon,
    this.enabled = true,
    this.style = const UiStyleData(
      tone: 'accent',
      paddingX: 12,
      paddingY: 6,
      radius: 999,
    ),
    this.semanticsId,
  });

  final String label;
  final bool selected;
  final ValueChanged<bool>? onSelected;
  final IconData? icon;
  final bool enabled;
  final UiStyleData style;
  final String? semanticsId;

  @override
  State<UiMixChip> createState() => _UiMixChipState();
}

class _UiMixChipState extends State<UiMixChip> {
  bool _hovered = false;
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final isInteractive = widget.enabled && widget.onSelected != null;
    final resolved = widget.style.resolve(
      hovered: _hovered && isInteractive,
      focused: _focused && isInteractive,
    );
    final activeBg = UiStyleMapper.resolveToneBackground(colors, resolved.tone);
    final bg = widget.selected ? activeBg : colors.recessed;
    final fg = widget.selected
        ? UiStyleMapper.resolveToneForeground(colors, resolved.tone)
        : colors.strong;
    final borderColor = _focused
        ? colors.focus
        : _hovered && isInteractive
        ? colors.accent
        : widget.selected
        ? activeBg
        : colors.hairline;
    final px = resolved.paddingX ?? tokens.p12;
    final py = resolved.paddingY ?? tokens.p6;
    final radius = resolved.radius ?? tokens.pill;

    final chip = NakedToggle(
      value: widget.selected,
      enabled: isInteractive,
      semanticLabel: widget.label,
      onChanged: isInteractive ? widget.onSelected! : null,
      onHoverChange: (hovered) {
        if (_hovered == hovered) return;
        setState(() => _hovered = hovered);
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
      },
      child: Box(
        style: BoxStyler()
            .color(isInteractive ? bg : bg.withValues(alpha: 0.45))
            .padding(
              EdgeInsetsGeometryMix.symmetric(horizontal: px, vertical: py),
            )
            .borderRadius(BorderRadiusGeometryMix.circular(radius))
            .border(
              BoxBorderMix.all(
                BorderSideMix.color(borderColor).width(_focused ? 1.5 : 1.0),
              ),
            )
            .animate(
              AnimationConfig.curve(
                duration: resolved.motion.duration,
                curve: resolved.motion.curve,
              ),
            ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (widget.icon != null) ...[
              Icon(
                widget.icon,
                size: 14,
                color: isInteractive ? fg : fg.withValues(alpha: 0.5),
              ),
              const SizedBox(width: 6),
            ],
            Text(
              widget.label,
              style: TextStyle(
                color: isInteractive ? fg : fg.withValues(alpha: 0.5),
                fontSize: 12.5,
                fontWeight: FontWeight.w500,
              ),
            ),
          ],
        ),
      ),
    );

    if (widget.semanticsId == null) return chip;
    return Semantics(
      identifier: widget.semanticsId,
      selected: widget.selected,
      enabled: isInteractive,
      child: chip,
    );
  }
}

// --- src/primitives/info_bar.dart ---
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

// --- src/primitives/progress.dart ---
typedef UiProgress = UiMixProgress;

/// Option B `mix`-styled linear or circular progress indicator; [value] is
/// clamped to 0..1 and ignored when [determinate] is false.
final class UiMixProgress extends StatelessWidget {
  const UiMixProgress({
    super.key,
    required this.determinate,
    required this.value,
    required this.label,
    this.error = false,
    this.ring = false,
  });

  final bool determinate;
  final double value;
  final String label;

  /// Draws it in the error tone: the work stopped.
  final bool error;

  /// Draws a 20px ring beside the label instead of a bar.
  final bool ring;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final fraction = value.isNaN ? 0.0 : value.clamp(0.0, 1.0);
    final color = error ? c.error : c.accent;

    if (ring) {
      return Row(
        mainAxisSize: MainAxisSize.min,
        spacing: 8,
        children: [
          SizedBox.square(
            dimension: 20,
            child: CircularProgressIndicator(
              value: determinate ? fraction : null,
              strokeWidth: 3,
              color: color,
              backgroundColor: c.recessed,
              semanticsLabel: label.isEmpty ? null : label,
            ),
          ),
          if (label.isNotEmpty)
            Flexible(child: Text(label, style: UiType.of(context).bodyMuted)),
        ],
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: [
        if (label.isNotEmpty) ...[
          Text(label, style: UiType.of(context).bodyMuted),
          const SizedBox(height: 8),
        ],
        Box(
          style: BoxStyler()
              .height(8)
              .borderRadius(BorderRadiusGeometryMix.circular(999)),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(999),
            child: LinearProgressIndicator(
              value: determinate ? fraction : null,
              minHeight: 8,
              color: color,
              backgroundColor: c.recessed,
              semanticsLabel: label.isEmpty ? null : label,
            ),
          ),
        ),
      ],
    );
  }
}

// --- src/primitives/skeleton.dart ---
final class UiSkeleton extends StatefulWidget {
  const UiSkeleton({
    super.key,
    this.width,
    this.height = 14,
    this.radius,
    this.circle = false,
    this.semanticsLabel,
  });

  final double? width;
  final double height;
  final double? radius;
  final bool circle;
  final String? semanticsLabel;

  @override
  State<UiSkeleton> createState() => _UiSkeletonState();
}

class _UiSkeletonState extends State<UiSkeleton>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller;

  @override
  void initState() {
    super.initState();
    _controller = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 1100),
    );
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final tokens = UiTokens.of(context);
    if (_controller.duration != tokens.skeleton) {
      _controller.duration = tokens.skeleton;
    }
    if (MediaQuery.disableAnimationsOf(context)) {
      _controller.stop();
      _controller.value = 0.5;
    } else if (!_controller.isAnimating) {
      _controller.repeat(reverse: true);
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final effectiveRadius = widget.circle
        ? tokens.pill
        : (widget.radius ?? tokens.radiusSmall);
    final effectiveWidth = widget.circle
        ? (widget.width ?? widget.height)
        : widget.width;

    return Semantics(
      label: widget.semanticsLabel,
      excludeSemantics: widget.semanticsLabel == null,
      child: AnimatedBuilder(
        animation: _controller,
        builder: (context, _) {
          final color = Color.lerp(
            c.recessed,
            c.hairline.withValues(alpha: 0.7),
            _controller.value,
          )!;
          var style = BoxStyler()
              .height(widget.height)
              .color(color)
              .borderRadius(BorderRadiusGeometryMix.circular(effectiveRadius));
          if (effectiveWidth != null) {
            style = style.width(effectiveWidth);
          }
          return Box(style: style);
        },
      ),
    );
  }
}

// --- src/primitives/toast.dart ---
enum UiTone {
  neutral,
  done,
  working,
  error;

  /// The tone for a wire int; anything unknown is [neutral].
  static UiTone fromWire(Object? value) =>
      value is int && value > 0 && value < values.length
      ? values[value]
      : neutral;
}

/// Default display lifetime for a kit toast.
const kitToastLifetime = Duration(seconds: 4);

OverlayEntry? _current;
Timer? _timer;

void _hide() {
  _timer?.cancel();
  _timer = null;
  final entry = _current;
  _current = null;
  entry?.remove();
}

/// Shows the Figma `.toast` over the root overlay of [context] (or [context]
/// itself when it is an [Overlay]'s, e.g. a navigator's `overlay.context`),
/// replacing any toast already up. It hides after [kitToastLifetime], when
/// its action runs, or when its overlay goes away.
void showKitToast(
  BuildContext context,
  String message, {
  UiTone tone = UiTone.neutral,
  String? actionLabel,
  VoidCallback? onAction,
}) {
  final overlay =
      Overlay.maybeOf(context, rootOverlay: true) ??
      switch (context) {
        StatefulElement(:final OverlayState state) => state,
        _ => null,
      };
  if (overlay == null) return;
  _hide();
  late final OverlayEntry entry;
  entry = OverlayEntry(
    builder: (context) => _Lifetime(
      onDispose: () {
        if (identical(_current, entry)) {
          _timer?.cancel();
          _timer = null;
          _current = null;
        }
      },
      child: Positioned(
        left: 0,
        right: 0,
        bottom: 22,
        child: Center(
          child: UiMixToastBody(
            message: message,
            tone: tone,
            actionLabel: actionLabel,
            onAction: onAction == null
                ? null
                : () {
                    _hide();
                    onAction();
                  },
          ),
        ),
      ),
    ),
  );
  _current = entry;
  overlay.insert(entry);
  _timer = Timer(kitToastLifetime, () {
    if (identical(_current, entry)) _hide();
  });
}

final class _Lifetime extends StatefulWidget {
  const _Lifetime({required this.onDispose, required this.child});
  final VoidCallback onDispose;
  final Widget child;
  @override
  State<_Lifetime> createState() => _LifetimeState();
}

class _LifetimeState extends State<_Lifetime> {
  @override
  void dispose() {
    widget.onDispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}

/// Legacy alias for [UiMixToastBody].
typedef KitToastBody = UiMixToastBody;

/// Option B `mix` Toast box: radius `controlRadius` (10), padding 10x12,
/// `strong` fill, `page` text in [UiType.toast], shadow-lg.
final class UiMixToastBody extends StatelessWidget {
  const UiMixToastBody({
    super.key,
    required this.message,
    this.tone = UiTone.neutral,
    this.actionLabel,
    this.onAction,
  });

  final String message;
  final UiTone tone;
  final String? actionLabel;
  final VoidCallback? onAction;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final style = UiType.of(context).toast.copyWith(color: c.page);
    final dot = switch (tone) {
      UiTone.neutral => null,
      UiTone.done => c.done,
      UiTone.working => c.working,
      UiTone.error => c.error,
    };
    final label = actionLabel;
    final shadows = UiShadows.lg(uiKitShadowBase(context));

    return Material(
      type: MaterialType.transparency,
      child: Semantics(
        liveRegion: true,
        child: Box(
          key: const ValueKey('ui_kit_toast'),
          style: BoxStyler()
              .color(c.strong)
              .padding(
                EdgeInsetsGeometryMix.symmetric(horizontal: 12, vertical: 10),
              )
              .borderRadius(
                BorderRadiusGeometryMix.circular(tokens.controlRadius),
              )
              .shadows([
                for (final s in shadows)
                  BoxShadowMix(
                    color: s.color,
                    offset: s.offset,
                    blurRadius: s.blurRadius,
                    spreadRadius: s.spreadRadius,
                  ),
              ]),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (dot != null) ...[
                Container(
                  width: 8,
                  height: 8,
                  decoration: BoxDecoration(color: dot, shape: BoxShape.circle),
                ),
                const SizedBox(width: 8),
              ],
              Flexible(child: Text(message, style: style)),
              if (label != null) ...[
                const SizedBox(width: 16),
                TextButton(
                  onPressed: onAction,
                  style: ButtonStyle(
                    padding: const WidgetStatePropertyAll(EdgeInsets.zero),
                    minimumSize: const WidgetStatePropertyAll(Size.zero),
                    tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                    foregroundColor: WidgetStatePropertyAll(c.page),
                    overlayColor: const WidgetStatePropertyAll(
                      Colors.transparent,
                    ),
                    textStyle: WidgetStatePropertyAll(
                      style.copyWith(
                        fontWeight: FontWeight.w700,
                        decoration: TextDecoration.underline,
                        decorationColor: c.page,
                      ),
                    ),
                  ),
                  child: Text(label),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

/// "Working": a loader that spins while a session or app is busy.
class WorkingSpinner extends StatefulWidget {
  const WorkingSpinner({super.key, this.size = 14, this.color});

  final double size;
  final Color? color;

  @override
  State<WorkingSpinner> createState() => _WorkingSpinnerState();
}

class _WorkingSpinnerState extends State<WorkingSpinner>
    with SingleTickerProviderStateMixin {
  late final AnimationController _a = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 900),
  );

  @override
  void initState() {
    super.initState();
    if (!WidgetsBinding.instance.runtimeType.toString().contains('Test')) {
      _a.repeat();
    }
  }

  @override
  void dispose() {
    _a.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final icon = Icon(
      LucideIcons.loaderCircle,
      size: widget.size,
      color: widget.color ?? UiColors.of(context).textMuted,
    );
    if (MediaQuery.disableAnimationsOf(context)) return icon;
    return RotationTransition(turns: _a, child: icon);
  }
}

enum TagTone { neutral, accent, done, working, error }

/// Compact status / metadata pill with optional icon.
class TagPill extends StatelessWidget {
  const TagPill({
    super.key,
    required this.label,
    this.tone = TagTone.neutral,
    this.icon,
  });

  final String label;
  final TagTone tone;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final (bg, fg) = switch (tone) {
      TagTone.neutral => (c.surfaceSunk, c.textMuted),
      TagTone.accent => (c.accentTint, c.accent),
      TagTone.done => (c.doneBg, c.done),
      TagTone.working => (c.workingBg, c.working),
      TagTone.error => (c.errorBg, c.error),
    };
    return Container(
      height: 22,
      padding: const EdgeInsets.symmetric(horizontal: 7),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
        border: Border.all(color: fg.withValues(alpha: 0.25)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[
            Icon(icon, size: 12, color: fg),
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
    );
  }
}
