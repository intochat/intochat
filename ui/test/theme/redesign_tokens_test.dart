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
  test('intochatLight carries the Figma :root tokens', () {
    const c = UiColors.intochatLight;
    expect(c.accentCopy, const Color(0xFF1F5EFF));
    expect(c.accentCopyHover, const Color(0xFF1541C7));
    expect(c.focus, const Color(0x591F5EFF));
    expect(c.userBubble, const Color(0xFF17140F));
    expect(c.userText, const Color(0xFFFBFAF7));
    expect(c.assistant, const Color(0xFFFFFFFF));
    expect(c.assistantText, const Color(0xFF2A251E));
    expect(c.assistantBorder, const Color(0xFFE4DFD5));
    expect(c.menuHover, const Color(0xFFF2EFE9));
  });

  test('intochatDark carries the Figma dark tokens', () {
    const c = UiColors.intochatDark;
    expect(c.accentCopy, const Color(0xFFFBFAF7));
    expect(c.accentCopyHover, const Color(0xFFF0ECE4));
    expect(c.focus, const Color(0x734B7DFF));
    expect(c.userBubble, const Color(0xFFFBFAF7));
    expect(c.userText, const Color(0xFF17140F));
    expect(c.assistant, const Color(0xFF2A251E));
    expect(c.assistantText, const Color(0xFFF0ECE4));
    expect(c.assistantBorder, const Color(0xFF423B31));
    expect(c.menuHover, const Color(0x99423B31));
  });

  test('canonical preset palettes derive default semantic tokens', () {
    for (final c in [
      UiColors.fluentAcrylicDark,
      UiColors.fluentAcrylicLight,
      UiColors.cyberSynapticDark,
      UiColors.linearCarbonDark,
      UiColors.nordicTacticalDark,
    ]) {
      expect(c.accentCopy, c.accent);
      expect(c.userBubble, c.strong);
      expect(c.userText, c.page);
      expect(c.assistant, c.surface);
      expect(c.assistantText, c.body);
      expect(c.assistantBorder, c.hairline);
      expect(c.menuHover, c.recessed);
      expect(c.focus.withValues(alpha: 1), c.accent);
    }
    expect(
      UiColors.forStoredTheme('midnight', brightness: Brightness.dark),
      UiColors.warmPaperDark,
    );
    expect(
      UiColors.forStoredTheme('paper', brightness: Brightness.light),
      UiColors.warmPaperLight,
    );
    expect(
      UiColors.forStoredTheme('lumen', brightness: Brightness.dark),
      UiColors.cyberSynapticDark,
    );
  });

  test('UiTokens radii and ease', () {
    const t = UiTokens();
    expect(t.radiusSmall, 8);
    expect(t.fieldRadius, 12);
    expect(t.panelRadius, 14);
    expect(t.modalRadius, 20);
    expect(t.pill, 999);
    expect(t.controlRadius, 10);
    expect(t.osBarHeight, 32);
    expect(t.rowHeight, 32);
    expect(t.fast, const Duration(milliseconds: 120));
    expect(t.base, const Duration(milliseconds: 200));
    expect(t.slow, const Duration(milliseconds: 340));
    expect(t.ease, const Cubic(0.2, 0.8, 0.2, 1));
  });

  test('focus halo, accent border and accentCopy contrast on surface', () {
    for (final c in [UiColors.intochatLight, UiColors.intochatDark]) {
      final focus = Color.alphaBlend(c.focus, c.surface);
      // The Figma focus halo is translucent by design (about 1.7:1 alone);
      // it is always paired with the opaque accent border checked below.
      expect(_contrast(focus, c.surface), greaterThanOrEqualTo(1.5));
      expect(_contrast(c.accent, c.surface), greaterThanOrEqualTo(3));
      expect(_contrast(c.accentCopy, c.surface), greaterThanOrEqualTo(3));
    }
  });

  test('UiShadows follow the Figma scale', () {
    const base = Color(0xFF17140F);
    void check(List<BoxShadow> s, double a, double dy, double blur) {
      expect(s, hasLength(1));
      expect(s.single.offset, Offset(0, dy));
      expect(s.single.blurRadius, blur);
      expect(s.single.color.a, closeTo(a, 0.005));
    }

    check(UiShadows.xs(base), .06, 1, 2);
    check(UiShadows.sm(base), .07, 1, 3);
    check(UiShadows.md(base), .08, 4, 12);
    check(UiShadows.lg(base), .10, 12, 32);
    check(UiShadows.xl(base), .14, 24, 60);
  });

  testWidgets('UiType exposes new styles', (tester) async {
    late UiTypeSet t;
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Builder(
          builder: (context) {
            t = UiType.of(context);
            return const SizedBox();
          },
        ),
      ),
    );
    expect(t.control.fontSize, 14);
    expect(t.control.fontWeight, FontWeight.w700);
    expect(t.fieldLabel.fontWeight, FontWeight.w600);
    expect(t.menuItem.fontWeight, FontWeight.w500);
    expect(t.menuLabel.fontSize, 11);
    expect(t.menuLabel.fontWeight, FontWeight.w500);
    expect(t.menuLabel.letterSpacing, closeTo(0.88, 0.001));
    expect(t.menuLabel.fontFamily, UiType.mono);
    expect(t.toast.fontSize, 13);
    expect(t.toast.fontWeight, FontWeight.w500);
  });
}
