import 'package:flutter/material.dart';

import '../primitives/chip.dart';
import '../primitives/style.dart';
import '../primitives/surface.dart';
import '../primitives/toggle.dart';
import 'mix_tokens.dart';
import 'ui_colors.dart';
import 'ui_tokens.dart';

/// Curated Fluent-inspired accent swatches for [ThemeStudioPanel].
const List<({String name, Color color})> uiThemeAccentSwatches = [
  (name: 'Cobalt', color: Color(0xFF1F5EFF)),
  (name: 'Fluent Blue', color: Color(0xFF0078D4)),
  (name: 'Indigo', color: Color(0xFF6366F1)),
  (name: 'Emerald', color: Color(0xFF10B981)),
  (name: 'Teal', color: Color(0xFF0D9488)),
  (name: 'Amber', color: Color(0xFFD97706)),
  (name: 'Crimson', color: Color(0xFFE11D48)),
  (name: 'Violet', color: Color(0xFF8B5CF6)),
];

/// Compact, Fluent XAML Theme Editor–inspired Live Theme Studio panel.
///
/// Built from Option B primitives ([UiMixSurface], [UiMixChip], [UiMixToggle])
/// and driven by [UiThemeSeeds] so every control in the tree live-morphs in
/// real time.
final class ThemeStudioPanel extends StatelessWidget {
  const ThemeStudioPanel({
    super.key,
    required this.seeds,
    required this.onChanged,
    this.semanticsId = 'theme-studio:panel',
  });

  final UiThemeSeeds seeds;
  final ValueChanged<UiThemeSeeds> onChanged;
  final String? semanticsId;

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final ratio = seeds.accentContrastRatio;
    final grade = seeds.accentContrastGrade;
    final isDark = seeds.brightness == Brightness.dark;

    return UiMixSurface(
      variant: UiMixSurfaceVariant.elevated,
      semanticsId: semanticsId,
      padding: EdgeInsets.all(tokens.p12),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Wrap(
            spacing: 8,
            runSpacing: 6,
            crossAxisAlignment: WrapCrossAlignment.center,
            alignment: WrapAlignment.spaceBetween,
            children: [
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    'Theme Studio',
                    style: TextStyle(
                      color: colors.strong,
                      fontSize: 13.5,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  const SizedBox(width: 8),
                  Semantics(
                    identifier: 'theme-studio:wcag-badge',
                    child: Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 8,
                        vertical: 3,
                      ),
                      decoration: BoxDecoration(
                        color: seeds.accent,
                        borderRadius: BorderRadius.circular(999),
                      ),
                      child: Text(
                        '${ratio.toStringAsFixed(1)}:1 · $grade',
                        style: TextStyle(
                          color: seeds.onAccent,
                          fontSize: 11,
                          fontWeight: FontWeight.w600,
                          fontFamily: 'JetBrains Mono',
                        ),
                      ),
                    ),
                  ),
                ],
              ),
              UiMixToggle(
                label: isDark ? 'Dark' : 'Light',
                value: isDark,
                semanticsId: 'theme-studio:dark-toggle',
                onChanged: (dark) => onChanged(
                  seeds.copyWith(
                    brightness: dark ? Brightness.dark : Brightness.light,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          _SectionLabel(label: 'Presets', color: colors.muted),
          const SizedBox(height: 6),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              for (final preset in UiThemeSeeds.presets)
                UiMixChip(
                  label: preset.label,
                  selected: seeds == preset.seeds,
                  semanticsId:
                      'theme-studio:preset-${preset.label.toLowerCase().replaceAll(' ', '-')}',
                  style: const UiStyleData(
                    tone: 'accent',
                    paddingX: 10,
                    paddingY: 5,
                    radius: 999,
                  ),
                  onSelected: (_) => onChanged(preset.seeds),
                ),
            ],
          ),
          const SizedBox(height: 10),
          _SectionLabel(label: 'Accent', color: colors.muted),
          const SizedBox(height: 6),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final swatch in uiThemeAccentSwatches)
                _AccentSwatch(
                  name: swatch.name,
                  color: swatch.color,
                  selected: seeds.accent.toARGB32() == swatch.color.toARGB32(),
                  ringColor: colors.strong,
                  onTap: () => onChanged(seeds.copyWith(accent: swatch.color)),
                ),
            ],
          ),
          const SizedBox(height: 10),
          Wrap(
            spacing: 16,
            runSpacing: 10,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  _SectionLabel(label: 'Density', color: colors.muted),
                  const SizedBox(height: 6),
                  Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: [
                      for (final d in UiDensity.values)
                        UiMixChip(
                          label: _capitalize(d.name),
                          selected: seeds.density == d,
                          semanticsId: 'theme-studio:density-${d.name}',
                          style: const UiStyleData(
                            tone: 'accent',
                            paddingX: 10,
                            paddingY: 5,
                            radius: 999,
                          ),
                          onSelected: (_) =>
                              onChanged(seeds.copyWith(density: d)),
                        ),
                    ],
                  ),
                ],
              ),
              Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  _SectionLabel(label: 'Radius', color: colors.muted),
                  const SizedBox(height: 6),
                  Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: [
                      for (final r in UiRadiusScale.values)
                        UiMixChip(
                          label: _capitalize(r.name),
                          selected: seeds.radiusScale == r,
                          semanticsId: 'theme-studio:radius-${r.name}',
                          style: const UiStyleData(
                            tone: 'accent',
                            paddingX: 10,
                            paddingY: 5,
                            radius: 999,
                          ),
                          onSelected: (_) =>
                              onChanged(seeds.copyWith(radiusScale: r)),
                        ),
                    ],
                  ),
                ],
              ),
            ],
          ),
        ],
      ),
    );
  }

  static String _capitalize(String value) =>
      value.isEmpty ? value : '${value[0].toUpperCase()}${value.substring(1)}';
}

final class _SectionLabel extends StatelessWidget {
  const _SectionLabel({required this.label, required this.color});

  final String label;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return Text(
      label.toUpperCase(),
      style: TextStyle(
        color: color,
        fontSize: 10.5,
        fontWeight: FontWeight.w600,
        letterSpacing: 0.7,
      ),
    );
  }
}

final class _AccentSwatch extends StatelessWidget {
  const _AccentSwatch({
    required this.name,
    required this.color,
    required this.selected,
    required this.ringColor,
    required this.onTap,
  });

  final String name;
  final Color color;
  final bool selected;
  final Color ringColor;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Tooltip(
      message: name,
      child: Semantics(
        identifier:
            'theme-studio:swatch-${name.toLowerCase().replaceAll(' ', '-')}',
        button: true,
        selected: selected,
        label: name,
        child: InkWell(
          onTap: onTap,
          customBorder: const CircleBorder(),
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 140),
            width: 24,
            height: 24,
            padding: const EdgeInsets.all(2),
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              border: Border.all(
                color: selected ? ringColor : Colors.transparent,
                width: 2,
              ),
            ),
            child: DecoratedBox(
              decoration: BoxDecoration(color: color, shape: BoxShape.circle),
            ),
          ),
        ),
      ),
    );
  }
}
