import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

double _contrast(Color a, Color b) {
  final la = a.computeLuminance();
  final lb = b.computeLuminance();
  final hi = la > lb ? la : lb;
  final lo = la > lb ? lb : la;
  return (hi + 0.05) / (lo + 0.05);
}

void main() {
  test('intochat values match Figma tokens', () {
    expect(UiColors.intochatLight.accent, const Color(0xFF1F5EFF));
    expect(UiColors.intochatDark.page, const Color(0xFF17140F));
    expect(UiColors.intochatDark.muted, const Color(0xFF9C9182));
    expect(UiColors.intochatDark.subtle, const Color(0xFF7C7264));
  });

  testWidgets('of() falls back to intochat light without extension', (
    tester,
  ) async {
    late UiColors resolved;
    await tester.pumpWidget(
      MaterialApp(
        home: Builder(
          builder: (context) {
            resolved = UiColors.of(context);
            return const SizedBox();
          },
        ),
      ),
    );
    expect(resolved, UiColors.intochatLight);
  });

  testWidgets('of() returns the registered extension', (tester) async {
    late UiColors resolved;
    await tester.pumpWidget(
      MaterialApp(
        theme: ThemeData(extensions: const [UiColors.intochatDark]),
        home: Builder(
          builder: (context) {
            resolved = UiColors.of(context);
            return const SizedBox();
          },
        ),
      ),
    );
    expect(resolved, UiColors.intochatDark);
  });

  test('lerp at 0 and 1 returns endpoints', () {
    expect(
      UiColors.intochatLight.lerp(UiColors.intochatDark, 0),
      UiColors.intochatLight,
    );
    expect(
      UiColors.intochatLight.lerp(UiColors.intochatDark, 1),
      UiColors.intochatDark,
    );
    expect(UiColors.intochatLight.lerp(null, 0.5), UiColors.intochatLight);
  });

  test('copyWith overrides one field', () {
    final c = UiColors.intochatLight.copyWith(accent: const Color(0xFF000000));
    expect(c.accent, const Color(0xFF000000));
    expect(c.page, UiColors.intochatLight.page);
  });

  test('body on page meets WCAG AA for intochat variants', () {
    for (final c in [UiColors.intochatLight, UiColors.intochatDark]) {
      expect(_contrast(c.body, c.page), greaterThanOrEqualTo(4.5));
    }
  });

  test('legacy stored theme names map to canonical presets at ingress', () {
    expect(
      UiColors.forStoredTheme('midnight', brightness: Brightness.light),
      UiColors.warmPaperDark,
    );
    expect(
      UiColors.forStoredTheme('dark', brightness: Brightness.light),
      UiColors.warmPaperDark,
    );
    expect(
      UiColors.forStoredTheme('paper', brightness: Brightness.dark),
      UiColors.warmPaperLight,
    );
    expect(
      UiColors.forStoredTheme('light', brightness: Brightness.dark),
      UiColors.warmPaperLight,
    );
    expect(
      UiColors.forStoredTheme('lumen', brightness: Brightness.light),
      UiColors.cyberSynapticDark,
    );
  });

  final all = {
    'intochatLight': UiColors.intochatLight,
    'intochatDark': UiColors.intochatDark,
    'fluentAcrylicDark': UiColors.fluentAcrylicDark,
    'fluentAcrylicLight': UiColors.fluentAcrylicLight,
    'cyberSynapticDark': UiColors.cyberSynapticDark,
    'fluentWarmMicaDark': UiColors.fluentWarmMicaDark,
    'fluentWarmMicaLight': UiColors.fluentWarmMicaLight,
    'linearCarbonDark': UiColors.linearCarbonDark,
    'nordicTacticalDark': UiColors.nordicTacticalDark,
    'nordicTacticalLight': UiColors.nordicTacticalLight,
  };

  test('every palette has readable body text on page', () {
    all.forEach((name, c) {
      expect(
        _contrast(c.body, c.page),
        greaterThanOrEqualTo(4.5),
        reason: name,
      );
    });
  });

  test('all palettes are pairwise distinct', () {
    final list = all.values.toList();
    for (var i = 0; i < list.length; i++) {
      for (var j = i + 1; j < list.length; j++) {
        expect(list[i], isNot(list[j]));
      }
    }
  });
}
