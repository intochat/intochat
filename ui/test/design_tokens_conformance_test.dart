import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

/// Authoritative `UK-5` conformance suite locking 1:1 hex, geometry, motion,
/// and typography parity with `E:\intochat\design\DESIGN.md` (§1–§7).
void main() {
  group('DESIGN.md §2 Colour Tokens — Light (Warm Paper)', () {
    const u = UiColors.intochatLight;
    const ic = IcColors.warmPaperLight;

    test('§2.1 Surfaces match DESIGN.md hexes', () {
      expect(u.surfacePage, const Color(0xFFFBFAF7));
      expect(u.surfaceSunk, const Color(0xFFF2EFE9));
      expect(u.surfaceCard, const Color(0xFFFFFFFF));
      expect(u.surfaceInk, const Color(0xFF17140F));

      expect(ic.surfacePage, const Color(0xFFFBFAF7));
      expect(ic.surfaceSunk, const Color(0xFFF2EFE9));
      expect(ic.surfaceCard, const Color(0xFFFFFFFF));
      expect(ic.surfaceInk, const Color(0xFF17140F));
    });

    test('§2.2 Text tokens match DESIGN.md hexes', () {
      expect(u.textStrong, const Color(0xFF17140F));
      expect(u.textBody, const Color(0xFF2A251E));
      expect(u.textMuted, const Color(0xFF7C7264));
      expect(u.textSubtle, const Color(0xFF9C9182));

      expect(ic.textStrong, const Color(0xFF17140F));
      expect(ic.textBody, const Color(0xFF2A251E));
      expect(ic.textMuted, const Color(0xFF7C7264));
      expect(ic.textSubtle, const Color(0xFF9C9182));
    });

    test('§2.3 Borders match DESIGN.md hexes', () {
      expect(u.borderHairline, const Color(0xFFE4DFD5));
      expect(u.borderInput, const Color(0xFFC2B9AB));
      expect(u.borderStrong, const Color(0xFF17140F));

      expect(ic.borderHairline, const Color(0xFFE4DFD5));
      expect(ic.borderInput, const Color(0xFFC2B9AB));
      expect(ic.borderStrong, const Color(0xFF17140F));
    });

    test('§2.4 Accent & focus ring match DESIGN.md hexes', () {
      expect(u.accent, const Color(0xFF1F5EFF));
      expect(u.accentHover, const Color(0xFF1541C7));
      expect(u.accentTint, const Color(0xFFE7EEFF));
      expect(u.onAccent, const Color(0xFFFFFFFF));
      expect(u.ring, const Color(0x591F5EFF));

      expect(ic.accent, const Color(0xFF1F5EFF));
      expect(ic.accentHover, const Color(0xFF1541C7));
      expect(ic.accentTint, const Color(0xFFE7EEFF));
      expect(ic.onAccent, const Color(0xFFFFFFFF));
      expect(ic.ring, const Color(0x591F5EFF));
    });

    test('§2.5 Status pairs match DESIGN.md hexes', () {
      expect(u.done, const Color(0xFF12A150));
      expect(u.doneBg, const Color(0xFFE1F6EA));
      expect(u.working, const Color(0xFFE8A317));
      expect(u.workingBg, const Color(0xFFFCF1DA));
      expect(u.error, const Color(0xFFE0402F));
      expect(u.errorBg, const Color(0xFFFCE7E3));
      expect(u.idle, const Color(0xFF9C9182));
      expect(u.needsYou, const Color(0xFF1F5EFF));
      expect(u.needsYouBg, const Color(0xFFE7EEFF));

      expect(ic.done, const Color(0xFF12A150));
      expect(ic.doneBg, const Color(0xFFE1F6EA));
      expect(ic.working, const Color(0xFFE8A317));
      expect(ic.workingBg, const Color(0xFFFCF1DA));
      expect(ic.error, const Color(0xFFE0402F));
      expect(ic.errorBg, const Color(0xFFFCE7E3));
      expect(ic.idle, const Color(0xFF9C9182));
      expect(ic.needsYou, const Color(0xFF1F5EFF));
      expect(ic.needsYouBg, const Color(0xFFE7EEFF));
    });

    test('§2.6–§2.8 Chrome, chartContext, Desktop tones & Overlays match', () {
      expect(u.chrome, const Color(0xE6FFFFFF));
      expect(u.chromeBorder, const Color(0xFFE4DFD5));
      expect(u.titleBarFocused, const Color(0xFFF2EFE9));
      expect(u.titleBarBlurred, const Color(0xFFFBFAF7));
      expect(u.chartContext, const Color(0xFF9C9182));
      expect(u.accentOnInk, const Color(0xFF8FB0FF));

      expect(u.desktopCream, const Color(0xFFF2EFE9));
      expect(u.desktopSand, const Color(0xFFE6DFD2));
      expect(u.desktopInk, const Color(0xFF2A251E));
      expect(DesktopTone.cream.color(ic), const Color(0xFFF2EFE9));
      expect(DesktopTone.sand.color(ic), const Color(0xFFE6DFD2));
      expect(DesktopTone.ink.color(ic), const Color(0xFF2A251E));

      expect(u.hoverOverlay, const Color(0x0A17140F));
      expect(u.barHoverOverlay, const Color(0x0F17140F));
      expect(u.selectedOverlay, const Color(0x1217140F));
    });
  });

  group('DESIGN.md §2 Colour Tokens — Dark (Warm Brown-Black)', () {
    const u = UiColors.intochatDark;
    const ic = IcColors.warmPaperDark;

    test('§2.1 Surfaces are warm brown-black, never cold blue-black', () {
      expect(u.surfacePage, const Color(0xFF17140F));
      expect(u.surfaceSunk, const Color(0xFF110F0B));
      expect(u.surfaceCard, const Color(0xFF2A251E));
      expect(u.surfaceInk, const Color(0xFF110F0B));
      // Warm brown-black invariant: red channel strictly exceeds blue channel.
      expect(u.surfacePage.r, greaterThan(u.surfacePage.b));
      expect(u.surfaceCard.r, greaterThan(u.surfaceCard.b));

      expect(ic.surfacePage, const Color(0xFF17140F));
      expect(ic.surfaceSunk, const Color(0xFF110F0B));
      expect(ic.surfaceCard, const Color(0xFF2A251E));
      expect(ic.surfaceInk, const Color(0xFF110F0B));
    });

    test('§2.2 Text tokens match DESIGN.md hexes', () {
      expect(u.textStrong, const Color(0xFFFBFAF7));
      expect(u.textBody, const Color(0xFFF0ECE4));
      expect(u.textMuted, const Color(0xFF9C9182));
      expect(u.textSubtle, const Color(0xFF7C7264));

      expect(ic.textStrong, const Color(0xFFFBFAF7));
      expect(ic.textBody, const Color(0xFFF0ECE4));
      expect(ic.textMuted, const Color(0xFF9C9182));
      expect(ic.textSubtle, const Color(0xFF7C7264));
    });

    test('§2.3 Borders match DESIGN.md hexes', () {
      expect(u.borderHairline, const Color(0xFF423B31));
      expect(u.borderInput, const Color(0xFF5E554A));
      expect(u.borderStrong, const Color(0xFFF0ECE4));

      expect(ic.borderHairline, const Color(0xFF423B31));
      expect(ic.borderInput, const Color(0xFF5E554A));
      expect(ic.borderStrong, const Color(0xFFF0ECE4));
    });

    test('§2.4 Accent & focus ring match DESIGN.md hexes (white onAccent)', () {
      expect(u.accent, const Color(0xFF4B7DFF));
      expect(u.accentHover, const Color(0xFF1F5EFF));
      expect(u.accentTint, const Color(0x294B7DFF));
      expect(u.onAccent, const Color(0xFFFFFFFF));
      expect(u.ring, const Color(0x734B7DFF));

      expect(ic.accent, const Color(0xFF4B7DFF));
      expect(ic.accentHover, const Color(0xFF1F5EFF));
      expect(ic.accentTint, const Color(0x294B7DFF));
      expect(ic.onAccent, const Color(0xFFFFFFFF));
      expect(ic.ring, const Color(0x734B7DFF));
    });

    test('§2.5 Status pairs match DESIGN.md hexes (16% dark status tints)', () {
      expect(u.done, const Color(0xFF8FE0AF));
      expect(u.doneBg, const Color(0x2912A150));
      expect(u.working, const Color(0xFFF6D28A));
      expect(u.workingBg, const Color(0x29E8A317));
      expect(u.error, const Color(0xFFF3A99F));
      expect(u.errorBg, const Color(0x29E0402F));
      expect(u.idle, const Color(0xFF7C7264));
      expect(u.needsYou, const Color(0xFF4B7DFF));
      expect(u.needsYouBg, const Color(0x294B7DFF));

      expect(ic.done, const Color(0xFF8FE0AF));
      expect(ic.doneBg, const Color(0x2912A150));
      expect(ic.working, const Color(0xFFF6D28A));
      expect(ic.workingBg, const Color(0x29E8A317));
      expect(ic.error, const Color(0xFFF3A99F));
      expect(ic.errorBg, const Color(0x29E0402F));
      expect(ic.idle, const Color(0xFF7C7264));
    });

    test('§2.6–§2.8 Chrome, chartContext, Desktop tones & Overlays match', () {
      expect(u.chrome, const Color(0xE62A251E));
      expect(u.chromeBorder, const Color(0xFF423B31));
      expect(u.titleBarFocused, const Color(0xFF332D25));
      expect(u.titleBarBlurred, const Color(0xFF2A251E));
      expect(u.chartContext, const Color(0xFF9C9182));

      expect(u.desktopCream, const Color(0xFF1D1914));
      expect(u.desktopSand, const Color(0xFF231E17));
      expect(u.desktopInk, const Color(0xFF0D0B08));
      expect(DesktopTone.cream.color(ic), const Color(0xFF1D1914));
      expect(DesktopTone.sand.color(ic), const Color(0xFF231E17));
      expect(DesktopTone.ink.color(ic), const Color(0xFF0D0B08));

      expect(u.hoverOverlay, const Color(0x14FBFAF7));
      expect(u.barHoverOverlay, const Color(0x1AFBFAF7));
      expect(u.selectedOverlay, const Color(0x1FFBFAF7));
      expect(u.onInk, const Color(0xFFFBFAF7));
      expect(ic.onInk, const Color(0xFFFBFAF7));
      expect(u.bubbleMine, const Color(0xFFFBFAF7));
      expect(u.onBubbleMine, const Color(0xFF17140F));
      expect(ic.bubbleMine, const Color(0xFFFBFAF7));
      expect(ic.onBubbleMine, const Color(0xFF17140F));
      expect(IcColors.none, const Color(0x00000000));
    });
  });

  group('DESIGN.md §1.10, §3, §5 & §7 Typography, Geometry & Motion', () {
    test('Inter everywhere: zero monospace families or fallbacks', () {
      expect(UiType.sansFamily, 'Inter');
      expect(UiType.displayFamily, 'InterDisplay');
      expect(UiType.monoFamily, 'Inter');
      expect(IcType.family, 'Inter');
      expect(IcType.display, 'InterDisplay');
      expect(IcType.monoFamily, 'Inter');

      for (final fb in UiType.monoFallback) {
        expect(fb.toLowerCase(), isNot(contains('mono')));
        expect(fb.toLowerCase(), isNot(contains('consolas')));
        expect(fb.toLowerCase(), isNot(contains('menlo')));
      }

      final tabular = UiType.tabularStyle(13);
      expect(tabular.fontFamily, 'Inter');
      expect(tabular.fontFeatures, contains(const FontFeature.tabularFigures()));

      final codeStyle = IcType.code(12, color: const Color(0xFF17140F));
      expect(codeStyle.fontFamily, 'Inter');
      expect(
        codeStyle.fontFeatures,
        contains(const FontFeature.tabularFigures()),
      );
    });

    test('Radii, bar heights, and motion durations match DESIGN.md §4–§7', () {
      const t = UiTokens();
      expect(t.xs, 6);
      expect(t.sm, 8);
      expect(t.button, 10);
      expect(t.input, 12);
      expect(t.card, 14);
      expect(t.bubble, 18);
      expect(t.panel, 20);
      expect(t.pill, 999);
      expect(t.osBarHeight, 32);
      expect(t.titleBarHeight, 38);
      expect(t.buttonHeight, 36);
      expect(t.buttonSmallHeight, 30);

      expect(IcRadius.bar, 4);
      expect(IcRadius.card, 14);
      expect(IcRadius.panel, 20);
      expect(IcSpace.s2, 2);
      expect(IcSpace.s6, 6);
      expect(IcSpace.s10, 10);
      expect(IcSpace.s14, 14);
      expect(IcSpace.s18, 18);
      expect(IcSpace.s28, 28);
      expect(IcSpace.s56, 56);

      expect(IcMotion.fast, const Duration(milliseconds: 120));
      expect(IcMotion.base, const Duration(milliseconds: 200));
      expect(IcMotion.slow, const Duration(milliseconds: 340));
      expect(IcMotion.skeleton, const Duration(milliseconds: 1100));
      expect(IcMotion.hintDelay, const Duration(milliseconds: 350));
    });

    testWidgets('IcTheme.of defaults to Warm Paper in light & dark modes', (
      tester,
    ) async {
      late IcColors resolvedLight;
      late IcColors resolvedDark;

      await tester.pumpWidget(
        MaterialApp(
          theme: ThemeData(brightness: Brightness.light),
          home: Builder(
            builder: (context) {
              resolvedLight = IcTheme.of(context);
              return const SizedBox();
            },
          ),
        ),
      );
      expect(resolvedLight.preset, IcStylePreset.warmPaper);
      expect(resolvedLight.surfacePage, const Color(0xFFFBFAF7));

      await tester.pumpWidget(
        MaterialApp(
          key: UniqueKey(),
          theme: ThemeData(brightness: Brightness.dark),
          home: Builder(
            builder: (context) {
              resolvedDark = IcTheme.of(context);
              return const SizedBox();
            },
          ),
        ),
      );
      expect(resolvedDark.preset, IcStylePreset.warmPaper);
      expect(resolvedDark.surfacePage, const Color(0xFF17140F));
    });
  });
}
