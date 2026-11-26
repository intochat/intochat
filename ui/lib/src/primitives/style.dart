import 'package:flutter/material.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'ui_control_surface.dart';

/// Optional state-specific style overrides mirroring C# `Mix.Style()`
/// `hover`, `press`, and `focus` blocks.
@immutable
final class UiStyleStateOverride {
  const UiStyleStateOverride({
    this.tone,
    this.typography,
    this.elevation,
    this.icon,
    this.motion,
    this.paddingX,
    this.paddingY,
    this.gap,
    this.radius,
    this.scale,
  });

  final String? tone;
  final String? typography;
  final String? elevation;
  final String? icon;
  final UiMotionPreset? motion;
  final double? paddingX;
  final double? paddingY;
  final double? gap;
  final double? radius;
  final double? scale;

  static UiStyleStateOverride? fromJson(Object? raw) {
    if (raw is! Map) return null;
    return UiStyleStateOverride(
      tone: raw['tone'] != null ? '${raw['tone']}'.trim().toLowerCase() : null,
      typography: raw['typography'] != null
          ? '${raw['typography']}'.trim().toLowerCase()
          : null,
      elevation: raw['elevation'] != null
          ? '${raw['elevation']}'.trim().toLowerCase()
          : null,
      icon: raw['icon'] as String?,
      motion: UiStyleData._parseMotionNullable(raw['motion']),
      paddingX: ((raw['paddingX'] ?? raw['px']) as num?)?.toDouble(),
      paddingY: ((raw['paddingY'] ?? raw['py']) as num?)?.toDouble(),
      gap: (raw['gap'] as num?)?.toDouble(),
      radius: (raw['radius'] as num?)?.toDouble(),
      scale: (raw['scale'] as num?)?.toDouble(),
    );
  }

  @override
  bool operator ==(Object other) =>
      other is UiStyleStateOverride &&
      other.tone == tone &&
      other.typography == typography &&
      other.elevation == elevation &&
      other.icon == icon &&
      other.motion == motion &&
      other.paddingX == paddingX &&
      other.paddingY == paddingY &&
      other.gap == gap &&
      other.radius == radius &&
      other.scale == scale;

  @override
  int get hashCode => Object.hash(
    tone,
    typography,
    elevation,
    icon,
    motion,
    paddingX,
    paddingY,
    gap,
    radius,
    scale,
  );
}

/// Style of a Mix primitive: tone, typography, elevation, padding, radius,
/// motion, and per-state overrides (`hover`, `press`, `focus`).
@immutable
final class UiStyleData {
  const UiStyleData({
    this.tone = 'default',
    this.typography = 'body',
    this.elevation = 'flat',
    this.icon,
    this.motion = UiMotionPreset.standard,
    this.paddingX,
    this.paddingY,
    this.gap,
    this.radius,
    this.scale,
    this.hover,
    this.press,
    this.focus,
  });

  final String tone;
  final String typography;
  final String elevation;
  final String? icon;
  final UiMotionPreset motion;
  final double? paddingX;
  final double? paddingY;
  final double? gap;
  final double? radius;
  final double? scale;
  final UiStyleStateOverride? hover;
  final UiStyleStateOverride? press;
  final UiStyleStateOverride? focus;

  factory UiStyleData.fromJson(Map<String, dynamic>? json) {
    if (json == null) return const UiStyleData();
    final rawTone =
        json['tone'] ?? _presetToTone(json['variant'] ?? json['preset']);
    return UiStyleData(
      tone: '${rawTone ?? 'default'}'.trim().toLowerCase(),
      typography: '${json['typography'] ?? 'body'}'.trim().toLowerCase(),
      elevation: '${json['elevation'] ?? 'flat'}'.trim().toLowerCase(),
      icon: json['icon'] as String?,
      motion: _parseMotion(json['motion']),
      paddingX: ((json['paddingX'] ?? json['px']) as num?)?.toDouble(),
      paddingY: ((json['paddingY'] ?? json['py']) as num?)?.toDouble(),
      gap: (json['gap'] as num?)?.toDouble(),
      radius: (json['radius'] as num?)?.toDouble(),
      scale: (json['scale'] as num?)?.toDouble(),
      hover: UiStyleStateOverride.fromJson(json['hover']),
      press: UiStyleStateOverride.fromJson(json['press']),
      focus: UiStyleStateOverride.fromJson(json['focus']),
    );
  }

  /// Parses a wire `style` value that may be either a structured C#
  /// `Mix.Style()` JSON object (`Map`) or a legacy string preset (`"primary"`).
  static UiStyleData? fromWire(Object? raw) {
    if (raw is Map<String, dynamic>) {
      return UiStyleData.fromJson(raw);
    }
    if (raw is Map) {
      return UiStyleData.fromJson(
        raw.map((key, value) => MapEntry('$key', value)),
      );
    }
    if (raw is String && raw.trim().isNotEmpty) {
      return UiStyleData.fromPreset(raw);
    }
    return null;
  }

  /// Creates a [UiStyleData] from a legacy string preset name (`primary`,
  /// `secondary`, `ghost`, `danger`, `link`).
  factory UiStyleData.fromPreset(String preset) {
    final normalized = preset.trim().toLowerCase();
    return switch (normalized) {
      'primary' => const UiStyleData(
        tone: 'accent',
        hover: UiStyleStateOverride(tone: 'accentstrong'),
      ),
      'ghost' => const UiStyleData(
        tone: 'ghost',
        hover: UiStyleStateOverride(tone: 'subtle'),
      ),
      'danger' => const UiStyleData(tone: 'danger'),
      'link' => const UiStyleData(tone: 'link'),
      'secondary' || '' => const UiStyleData(
        tone: 'secondary',
        hover: UiStyleStateOverride(tone: 'subtle'),
      ),
      _ => UiStyleData(tone: normalized),
    };
  }

  /// Merges active [focus], [hover], and [press] state overrides (in increasing
  /// precedence) onto this base style.
  UiStyleData resolve({
    bool hovered = false,
    bool pressed = false,
    bool focused = false,
  }) {
    final focusOv = focused ? focus : null;
    final hoverOv = hovered ? hover : null;
    final pressOv = pressed ? press : null;
    if (focusOv == null && hoverOv == null && pressOv == null) return this;

    return UiStyleData(
      tone: pressOv?.tone ?? hoverOv?.tone ?? focusOv?.tone ?? tone,
      typography:
          pressOv?.typography ??
          hoverOv?.typography ??
          focusOv?.typography ??
          typography,
      elevation:
          pressOv?.elevation ??
          hoverOv?.elevation ??
          focusOv?.elevation ??
          elevation,
      icon: pressOv?.icon ?? hoverOv?.icon ?? focusOv?.icon ?? icon,
      motion: pressOv?.motion ?? hoverOv?.motion ?? focusOv?.motion ?? motion,
      paddingX:
          pressOv?.paddingX ??
          hoverOv?.paddingX ??
          focusOv?.paddingX ??
          paddingX,
      paddingY:
          pressOv?.paddingY ??
          hoverOv?.paddingY ??
          focusOv?.paddingY ??
          paddingY,
      gap: pressOv?.gap ?? hoverOv?.gap ?? focusOv?.gap ?? gap,
      radius: pressOv?.radius ?? hoverOv?.radius ?? focusOv?.radius ?? radius,
      scale: pressOv?.scale ?? hoverOv?.scale ?? focusOv?.scale ?? scale,
      hover: hover,
      press: press,
      focus: focus,
    );
  }

  static String? _presetToTone(Object? raw) {
    if (raw is! String || raw.trim().isEmpty) return null;
    return switch (raw.trim().toLowerCase()) {
      'primary' => 'accent',
      'secondary' => 'secondary',
      'ghost' => 'ghost',
      'danger' => 'danger',
      'link' => 'link',
      final other => other,
    };
  }

  static UiMotionPreset _parseMotion(Object? raw) =>
      _parseMotionNullable(raw) ?? UiMotionPreset.standard;

  static UiMotionPreset? _parseMotionNullable(Object? raw) {
    if (raw == null) return null;
    final name = '$raw'.trim().toLowerCase();
    for (final preset in UiMotionPreset.values) {
      if (preset.name == name) return preset;
    }
    return null;
  }

  @override
  bool operator ==(Object other) =>
      other is UiStyleData &&
      other.tone == tone &&
      other.typography == typography &&
      other.elevation == elevation &&
      other.icon == icon &&
      other.motion == motion &&
      other.paddingX == paddingX &&
      other.paddingY == paddingY &&
      other.gap == gap &&
      other.radius == radius &&
      other.scale == scale &&
      other.hover == hover &&
      other.press == press &&
      other.focus == focus;

  @override
  int get hashCode => Object.hash(
    tone,
    typography,
    elevation,
    icon,
    motion,
    paddingX,
    paddingY,
    gap,
    radius,
    scale,
    hover,
    press,
    focus,
  );
}

/// Single authoritative mapper converting C# [UiStyleData] into Flutter Mix
/// [BoxStyler], shadows, scale, and Inter [TextStyle] so C#-authored padding,
/// radius, tone, typography, elevation, motion, and state overrides render
/// with 1-to-1 parity across every UI control.
abstract final class UiStyleMapper {
  static Color resolveToneBackground(UiColors colors, String tone) =>
      switch (tone.trim().toLowerCase()) {
        'accent' || 'primary' => colors.accent,
        'accentstrong' || 'accent-strong' => colors.accentHover,
        'accenttint' || 'accent-tint' => colors.accentTint,
        'success' || 'done' => colors.done,
        'warning' || 'working' => colors.working,
        'danger' || 'error' => colors.error,
        'subtle' || 'recessed' => colors.recessed,
        'elevated' => colors.elevated,
        'ghost' || 'link' || 'transparent' => Colors.transparent,
        'card' || 'secondary' => colors.surface,
        'canvas' => colors.page,
        _ => colors.surface,
      };

  static Color resolveToneForeground(UiColors colors, String tone) =>
      switch (tone.trim().toLowerCase()) {
        'accent' ||
        'primary' ||
        'accentstrong' ||
        'accent-strong' => colors.onAccent,
        'success' ||
        'done' ||
        'warning' ||
        'working' ||
        'danger' ||
        'error' => Colors.white,
        'accenttint' || 'accent-tint' || 'link' => colors.accentCopy,
        'ghost' => colors.body,
        _ => colors.strong,
      };

  static List<BoxShadow> elevationShadows(UiColors colors, String elevation) =>
      switch (elevation.trim().toLowerCase()) {
        'subtle' || 'xs' => UiShadows.xs(colors),
        'raised' || 'sm' => UiShadows.sm(colors),
        'floating' || 'md' => UiShadows.md(colors),
        'overlay' || 'lg' => UiShadows.lg(colors),
        _ => const <BoxShadow>[],
      };

  static TextStyle textStyle(
    BuildContext context,
    UiStyleData style, {
    bool isEnabled = true,
    Color? colorOverride,
  }) {
    final colors = UiColors.of(context);
    final baseFg = colorOverride ?? resolveToneForeground(colors, style.tone);
    final fg = isEnabled ? baseFg : baseFg.withValues(alpha: 0.5);

    return switch (style.typography.trim().toLowerCase()) {
      'display' => UiType.displayStyle(24, weight: FontWeight.w700, color: fg),
      'heading' ||
      'title' => UiType.displayStyle(17, weight: FontWeight.w700, color: fg),
      'label' => UiType.textStyle(13.5, weight: FontWeight.w600, color: fg),
      'caption' => UiType.textStyle(12, weight: FontWeight.w500, color: fg),
      'metric' => UiType.textStyle(
        16,
        weight: FontWeight.w600,
        color: fg,
        tnum: true,
      ),
      'tabular' ||
      'code' => UiType.tabularStyle(13, weight: FontWeight.w500, color: fg),
      _ => UiType.textStyle(13.5, weight: FontWeight.w600, color: fg),
    };
  }

  static double resolveScale(
    UiStyleData style, {
    bool isHovered = false,
    bool isPressed = false,
    bool isFocused = false,
    bool isEnabled = true,
  }) {
    if (!isEnabled) return 1.0;
    final resolved = style.resolve(
      hovered: isHovered,
      pressed: isPressed,
      focused: isFocused,
    );
    return resolved.scale ?? (isPressed ? 0.97 : 1.0);
  }

  static BoxStyler boxStyler(
    BuildContext context,
    UiStyleData style, {
    bool isHovered = false,
    bool isPressed = false,
    bool isFocused = false,
    bool isEnabled = true,
  }) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);

    final activeHover = isHovered && isEnabled;
    final activePress = isPressed && isEnabled;
    final activeFocus = isFocused && isEnabled;

    final resolved = style.resolve(
      hovered: activeHover,
      pressed: activePress,
      focused: activeFocus,
    );

    final hasExplicitPressTone = activePress && style.press?.tone != null;
    final hasExplicitHoverTone = activeHover && style.hover?.tone != null;

    final baseBg = resolveToneBackground(colors, resolved.tone);
    final Color effectiveBg;
    if (!isEnabled) {
      effectiveBg = baseBg == Colors.transparent
          ? Colors.transparent
          : baseBg.withValues(alpha: 0.45);
    } else if (activePress && !hasExplicitPressTone) {
      effectiveBg = baseBg == Colors.transparent
          ? colors.recessed
          : baseBg.withValues(alpha: 0.82);
    } else if (activeHover && !hasExplicitHoverTone) {
      effectiveBg = baseBg == Colors.transparent
          ? colors.recessed.withValues(alpha: 0.7)
          : baseBg.withValues(alpha: 0.92);
    } else {
      effectiveBg = baseBg;
    }

    final px = resolved.paddingX ?? tokens.spaceMedium;
    final py = resolved.paddingY ?? tokens.spaceSmall;
    final r = resolved.radius ?? tokens.controlRadius;

    final isGhostOrLink = resolved.tone == 'ghost' || resolved.tone == 'link';
    final borderColor = activeFocus
        ? colors.focus
        : activeHover
        ? colors.accent
        : isGhostOrLink
        ? Colors.transparent
        : colors.hairline;

    var styler = BoxStyler()
        .color(effectiveBg)
        .padding(EdgeInsetsGeometryMix.symmetric(horizontal: px, vertical: py))
        .borderRadius(BorderRadiusGeometryMix.circular(r))
        .border(
          BoxBorderMix.all(
            BorderSideMix.color(borderColor).width(activeFocus ? 1.5 : 1.0),
          ),
        )
        .animate(
          AnimationConfig.curve(
            duration: resolved.motion.duration,
            curve: resolved.motion.curve,
          ),
        );

    final shadows = elevationShadows(colors, resolved.elevation);
    if (shadows.isNotEmpty) {
      styler = styler.shadows([
        for (final s in shadows)
          BoxShadowMix(
            color: s.color,
            offset: s.offset,
            blurRadius: s.blurRadius,
            spreadRadius: s.spreadRadius,
          ),
      ]);
    }

    return styler;
  }

  /// Builds a declarative `mix` [BoxStyler] with native `.onFocused`,
  /// `.onHovered`, `.onPressed`, and `.onDisabled` widget-state variants for
  /// `PressableBox` and `WidgetState`-driven containers.
  static BoxStyler widgetStateBoxStyler(
    BuildContext context,
    UiStyleData style,
  ) {
    final base = boxStyler(context, style);
    final focused = boxStyler(context, style, isFocused: true);
    final hovered = boxStyler(context, style, isHovered: true);
    final pressed = boxStyler(context, style, isPressed: true);
    final disabled = boxStyler(context, style, isEnabled: false);

    return base
        .onFocused(focused)
        .onHovered(hovered)
        .onPressed(pressed)
        .onDisabled(disabled);
  }
}

/// Option B flagship control: headless `NakedButton` (accessibility, keyboard
/// focus, hover/press/focus states) + `mix` `Box` driven directly by C#
/// [UiStyleData] (`Mix.Style()`).
final class UiMixButton extends StatefulWidget {
  const UiMixButton({
    super.key,
    required this.label,
    this.onPressed,
    this.style = const UiStyleData(tone: 'accent', paddingX: 16, paddingY: 10),
    this.icon,
    this.enabled = true,
    this.fullWidth = false,
    this.focusNode,
    this.tooltip,
    this.semanticsId,
  });

  final String label;
  final VoidCallback? onPressed;
  final UiStyleData style;
  final String? icon;
  final bool enabled;
  final bool fullWidth;
  final FocusNode? focusNode;
  final String? tooltip;
  final String? semanticsId;

  @override
  State<UiMixButton> createState() => _UiMixButtonState();
}

class _UiMixButtonState extends State<UiMixButton> {
  bool _hovered = false;
  bool _pressed = false;
  bool _focused = false;

  bool get _interactive => widget.enabled && widget.onPressed != null;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final resolved = widget.style.resolve(
      hovered: _hovered && _interactive,
      pressed: _pressed && _interactive,
      focused: _focused && _interactive,
    );
    final fg = UiStyleMapper.resolveToneForeground(colors, resolved.tone);
    final effectiveFg = _interactive ? fg : fg.withValues(alpha: 0.5);
    final iconName = widget.icon ?? resolved.icon;
    final iconData = iconName != null && iconName.isNotEmpty
        ? uiIcon(iconName)
        : null;
    final gap = resolved.gap ?? 6.0;
    final textStyle = UiStyleMapper.textStyle(
      context,
      resolved,
      isEnabled: _interactive,
      colorOverride: fg,
    );

    final labelWidget = Text(
      widget.label,
      style: textStyle,
      maxLines: 1,
      overflow: TextOverflow.ellipsis,
    );

    final content = iconData == null
        ? labelWidget
        : Row(
            mainAxisSize: widget.fullWidth
                ? MainAxisSize.max
                : MainAxisSize.min,
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(iconData, size: 15, color: effectiveFg),
              SizedBox(width: gap),
              Flexible(child: labelWidget),
            ],
          );

    Widget box = Box(
      style: UiStyleMapper.boxStyler(
        context,
        widget.style,
        isHovered: _hovered,
        isPressed: _pressed,
        isFocused: _focused,
        isEnabled: _interactive,
      ),
      child: widget.fullWidth ? Center(child: content) : content,
    );
    if (widget.fullWidth) {
      box = SizedBox(width: double.infinity, child: box);
    }

    Widget button = NakedButton(
      focusNode: widget.focusNode,
      enabled: _interactive,
      onPressed: widget.onPressed,
      onHoverChange: (hovered) {
        if (_hovered == hovered) return;
        setState(() => _hovered = hovered);
      },
      onPressChange: (pressed) {
        if (_pressed == pressed) return;
        setState(() => _pressed = pressed);
      },
      onFocusChange: (focused) {
        if (_focused == focused) return;
        setState(() => _focused = focused);
      },
      child: AnimatedScale(
        scale: UiStyleMapper.resolveScale(
          widget.style,
          isHovered: _hovered,
          isPressed: _pressed,
          isFocused: _focused,
          isEnabled: _interactive,
        ),
        duration: resolved.motion.duration,
        curve: resolved.motion.curve,
        child: box,
      ),
    );

    final tip = widget.tooltip?.trim();
    if (tip != null && tip.isNotEmpty) {
      button = Tooltip(message: tip, child: button);
    }

    if (widget.semanticsId == null) return button;
    return Semantics(
      identifier: widget.semanticsId,
      button: true,
      enabled: _interactive,
      child: button,
    );
  }
}
