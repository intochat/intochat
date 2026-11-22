import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  group('WCAG contrast calculation', () {
    test('black on white is 21:1 and AAA', () {
      final ratio = contrastRatio(Colors.black, Colors.white);
      expect(ratio, closeTo(21.0, 0.01));
      expect(wcagContrastGrade(ratio), 'AAA');
    });

    test('identical colors have 1:1 contrast and Fail', () {
      final ratio = contrastRatio(
        const Color(0xFF1F5EFF),
        const Color(0xFF1F5EFF),
      );
      expect(ratio, closeTo(1.0, 0.01));
      expect(wcagContrastGrade(ratio), 'Fail');
    });

    test(
      'deriveOnAccent picks dark ink for bright accents and white for dark',
      () {
        expect(
          deriveOnAccent(const Color(0xFF0078D4)),
          const Color(0xFFFFFFFF),
        );
        expect(
          deriveOnAccent(const Color(0xFFFACC15)),
          const Color(0xFF17140F),
        );
      },
    );
  });

  group('UiThemeSeeds derivation', () {
    test('named presets have expected brightness and readable onAccent', () {
      for (final preset in UiThemeSeeds.presets) {
        final s = preset.seeds;
        expect(s.accentContrastRatio, greaterThanOrEqualTo(3.0));
        final colors = s.toUiColors();
        expect(colors.accent, s.accent);
        expect(colors.onAccent, s.onAccent);
        expect(colors.brightness, s.brightness);
      }
    });

    test('density scales spacing and control heights', () {
      final compact = UiThemeSeeds.warmPaper
          .copyWith(density: UiDensity.compact)
          .toUiTokens();
      final comfortable = UiThemeSeeds.warmPaper
          .copyWith(density: UiDensity.comfortable)
          .toUiTokens();
      final spacious = UiThemeSeeds.warmPaper
          .copyWith(density: UiDensity.spacious)
          .toUiTokens();

      expect(compact.spaceMedium, lessThan(comfortable.spaceMedium));
      expect(spacious.spaceMedium, greaterThan(comfortable.spaceMedium));
      expect(compact.buttonHeight, lessThan(comfortable.buttonHeight));
      expect(spacious.buttonHeight, greaterThan(comfortable.buttonHeight));
    });

    test('radiusScale scales control and card radii', () {
      final sharp = UiThemeSeeds.warmPaper
          .copyWith(radiusScale: UiRadiusScale.sharp)
          .toUiTokens();
      final fluent = UiThemeSeeds.warmPaper
          .copyWith(radiusScale: UiRadiusScale.fluent)
          .toUiTokens();
      final pill = UiThemeSeeds.warmPaper
          .copyWith(radiusScale: UiRadiusScale.pill)
          .toUiTokens();

      expect(sharp.controlRadius, lessThan(fluent.controlRadius));
      expect(pill.controlRadius, greaterThan(fluent.controlRadius));
      expect(sharp.card, lessThan(fluent.card));
      expect(pill.card, greaterThan(fluent.card));
    });

    test('reduced motion zeroes animation durations', () {
      final reduced = UiThemeSeeds.warmPaper
          .copyWith(motion: UiMotionMode.reduced)
          .toUiTokens();
      expect(reduced.fast, Duration.zero);
      expect(reduced.base, Duration.zero);
      expect(reduced.slow, Duration.zero);
    });

    testWidgets(
      'UiSeedThemeScope synchronizes MixScope and UiColors/UiTokens',
      (tester) async {
        Color? resolvedMixAccent;
        Color? resolvedUiAccent;
        double? resolvedMixSpace;
        double? resolvedUiSpace;

        await tester.pumpWidget(
          MaterialApp(
            home: UiSeedThemeScope(
              seeds: UiThemeSeeds.compactPro,
              child: Builder(
                builder: (context) {
                  resolvedMixAccent = UiMixTokens.accent.resolve(context);
                  resolvedUiAccent = UiColors.of(context).accent;
                  resolvedMixSpace = UiMixTokens.spaceMedium.resolve(context);
                  resolvedUiSpace = UiTokens.of(context).spaceMedium;
                  return const SizedBox.shrink();
                },
              ),
            ),
          ),
        );

        expect(resolvedMixAccent, UiThemeSeeds.compactPro.accent);
        expect(resolvedUiAccent, UiThemeSeeds.compactPro.accent);
        expect(resolvedMixSpace, resolvedUiSpace);
      },
    );
  });

  group('ThemeStudioPanel live interaction', () {
    testWidgets(
      'selecting preset, density, radius, and dark mode updates tree',
      (tester) async {
        UiThemeSeeds current = UiThemeSeeds.warmPaper;
        Color? observedAccent;
        Brightness? observedBrightness;
        double? observedRadius;

        await tester.pumpWidget(
          StatefulBuilder(
            builder: (context, setState) {
              return MaterialApp(
                home: UiSeedThemeScope(
                  seeds: current,
                  child: Scaffold(
                    body: SingleChildScrollView(
                      child: Column(
                        children: [
                          ThemeStudioPanel(
                            seeds: current,
                            onChanged: (next) => setState(() => current = next),
                          ),
                          Builder(
                            builder: (ctx) {
                              observedAccent = UiColors.of(ctx).accent;
                              observedBrightness = UiColors.of(ctx).brightness;
                              observedRadius = UiTokens.of(ctx).controlRadius;
                              return const SizedBox.shrink();
                            },
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
              );
            },
          ),
        );

        expect(observedAccent, UiThemeSeeds.warmPaper.accent);
        expect(observedBrightness, Brightness.light);

        // Tap the "Emerald" preset chip.
        await tester.tap(find.text('Emerald'));
        await tester.pumpAndSettle();
        expect(current, UiThemeSeeds.emeraldStudio);
        expect(observedAccent, UiThemeSeeds.emeraldStudio.accent);
        expect(observedBrightness, Brightness.dark);

        // Tap "Sharp" radius chip.
        await tester.tap(find.text('Sharp'));
        await tester.pumpAndSettle();
        expect(current.radiusScale, UiRadiusScale.sharp);
        expect(
          observedRadius,
          UiThemeSeeds.emeraldStudio
              .copyWith(radiusScale: UiRadiusScale.sharp)
              .toUiTokens()
              .controlRadius,
        );

        // Tap "Compact" density chip.
        await tester.tap(find.text('Compact'));
        await tester.pumpAndSettle();
        expect(current.density, UiDensity.compact);
      },
    );
  });
}
