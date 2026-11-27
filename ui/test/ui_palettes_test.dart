import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('palettes list intochat first and only canonical presets', () {
    expect(UiTheme.palettes.first, 'intochat');
    expect(
      UiTheme.palettes,
      containsAll([
        'intochat',
        'fluent-acrylic',
        'spatial-glass',
        'cyber-synaptic',
        'warm-paper',
        'fluent-warm-mica',
        'linear-carbon',
        'nordic-tactical',
      ]),
    );
    expect(UiTheme.palettes, isNot(contains('midnight')));
    expect(UiTheme.palettes, isNot(contains('paper')));
    expect(UiTheme.palettes, isNot(contains('lumen')));
  });

  test('stored theme values resolve to a palette and never throw', () {
    expect(UiTheme.paletteName(null), 'intochat');
    expect(UiTheme.paletteName(''), 'intochat');
    expect(UiTheme.paletteName('system'), 'intochat');
    expect(UiTheme.paletteName('nonsense'), 'intochat');
    expect(UiTheme.paletteName('dark'), 'warm-paper');
    expect(UiTheme.paletteName('midnight'), 'warm-paper');
    expect(UiTheme.paletteName('light'), 'warm-paper');
    expect(UiTheme.paletteName('paper'), 'warm-paper');
    expect(UiTheme.paletteName('lumen'), 'cyber-synaptic');
    for (final name in UiTheme.palettes) {
      expect(UiTheme.paletteName(name), name);
    }
    for (final stored in [
      null,
      '',
      'system',
      'dark',
      'light',
      'midnight',
      'paper',
      'lumen',
      'x',
      ...UiTheme.palettes,
    ]) {
      expect(UiTheme.light(stored), isA<ThemeData>());
      expect(UiTheme.dark(stored), isA<ThemeData>());
    }
  });

  test('theme modes per palette', () {
    expect(UiTheme.modeFor('intochat'), ThemeMode.system);
    expect(UiTheme.modeFor(null), ThemeMode.system);
    expect(UiTheme.modeFor('paper'), ThemeMode.light);
    expect(UiTheme.modeFor('light'), ThemeMode.light);
    expect(UiTheme.modeFor('midnight'), ThemeMode.dark);
    expect(UiTheme.modeFor('lumen'), ThemeMode.dark);
  });

  test('intochat themes carry UiColors, accent primary and Inter type', () {
    final light = UiTheme.light('intochat');
    final dark = UiTheme.dark('intochat');
    expect(light.extension<UiColors>(), UiColors.intochatLight);
    expect(dark.extension<UiColors>(), UiColors.intochatDark);
    expect(light.extension<UiTokens>(), isNotNull);
    expect(dark.scaffoldBackgroundColor, const Color(0xFF17140F));
    expect(light.scaffoldBackgroundColor, const Color(0xFFFBFAF7));
    expect(light.colorScheme.primary, UiColors.intochatLight.accent);
    expect(dark.colorScheme.primary, UiColors.intochatDark.accent);
    expect(light.brightness, Brightness.light);
    expect(dark.brightness, Brightness.dark);
    expect(light.textTheme.bodyMedium!.fontFamily, contains('Inter'));
  });

  test('legacy stored theme names map to canonical presets at ingress', () {
    expect(
      UiTheme.dark('midnight').extension<UiColors>(),
      UiColors.warmPaperDark,
    );
    expect(
      UiTheme.light('midnight').extension<UiColors>(),
      UiColors.warmPaperDark,
    );
    expect(
      UiTheme.light('paper').extension<UiColors>(),
      UiColors.warmPaperLight,
    );
    expect(
      UiTheme.dark('paper').extension<UiColors>(),
      UiColors.warmPaperLight,
    );
    expect(
      UiTheme.dark('lumen').extension<UiColors>(),
      UiColors.cyberSynapticDark,
    );
    expect(
      UiTheme.light('lumen').extension<UiColors>(),
      UiColors.cyberSynapticDark,
    );
  });

  test(
    'legacy stored themes build directly from their mapped canonical UiColors',
    () {
      void check(ThemeData t, UiColors palette) {
        expect(t.scaffoldBackgroundColor, palette.page);
        expect(t.colorScheme.primary, palette.accent);
        expect(t.colorScheme.surface, palette.surface);
        expect(t.colorScheme.onSurface, palette.body);
      }

      check(UiTheme.dark('midnight'), UiColors.warmPaperDark);
      check(UiTheme.light('paper'), UiColors.warmPaperLight);
      check(UiTheme.dark('lumen'), UiColors.cyberSynapticDark);
    },
  );

  testWidgets('UiType.of uses the ambient UiColors', (tester) async {
    late UiTypeSet type;
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.light('intochat'),
        home: Builder(
          builder: (context) {
            type = UiType.of(context);
            return const SizedBox();
          },
        ),
      ),
    );
    expect(type.body.color, UiColors.intochatLight.body);
    expect(type.bodyMuted.color, UiColors.intochatLight.muted);
    expect(type.title.color, UiColors.intochatLight.strong);
    expect(type.body.fontFamily, contains('Inter'));
    expect(type.meta.fontFamily, contains('Inter'));
    expect(type.heading.fontWeight, FontWeight.w700);
  });

  test('intochat textTheme pins Inter and InterDisplay families', () {
    final t = UiTheme.light('intochat').textTheme;
    expect(t.headlineMedium!.fontFamily, contains('InterDisplay'));
    expect(t.bodyMedium!.fontFamily, contains('Inter'));
  });

  testWidgets('lerped intochat UiColors keep Inter fonts', (tester) async {
    late UiTypeSet type;
    final mid = UiColors.intochatLight.lerp(UiColors.intochatDark, 0.5);
    final base = UiTheme.light('intochat');
    await tester.pumpWidget(
      MaterialApp(
        theme: base.copyWith(
          extensions: [
            ...base.extensions.values.where((e) => e is! UiColors),
            mid,
          ],
        ),
        home: Builder(
          builder: (context) {
            type = UiType.of(context);
            return const SizedBox();
          },
        ),
      ),
    );
    expect(type.body.fontFamily, contains('Inter'));
    expect(type.body.color, mid.body);
  });

  test('lumen maps to cyberSynaptic dark and differs from midnight (warmPaper dark)', () {
    final lumen = UiTheme.dark('lumen');
    final midnight = UiTheme.dark('midnight');
    expect(lumen.brightness, Brightness.dark);
    expect(lumen.scaffoldBackgroundColor.computeLuminance(), lessThan(0.02));
    final primary = lumen.colorScheme.primary;
    expect(primary.g, greaterThan(primary.r));
    expect(primary.g, greaterThan(primary.b));
    expect(
      midnight.scaffoldBackgroundColor,
      UiTheme.forPreset(
        IcStylePreset.warmPaper,
        brightness: Brightness.dark,
      ).scaffoldBackgroundColor,
    );
    expect(
      lumen.scaffoldBackgroundColor,
      isNot(midnight.scaffoldBackgroundColor),
    );
  });
}
