import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('old theme values map to the named palettes', () {
    expect(UiTheme.paletteName('dark'), 'midnight');
    expect(UiTheme.paletteName('light'), 'paper');
    expect(UiTheme.paletteName('system'), 'midnight');
    expect(UiTheme.paletteName('nonsense'), 'midnight');
    for (final name in UiTheme.palettes) {
      expect(UiTheme.paletteName(name), name);
    }
  });

  test('paper is the only light palette', () {
    expect(UiTheme.modeFor('paper'), ThemeMode.light);
    expect(UiTheme.modeFor('midnight'), ThemeMode.dark);
    expect(UiTheme.modeFor('lumen'), ThemeMode.dark);
    expect(UiTheme.modeFor('light'), ThemeMode.light);
  });

  test('lumen is green on near-black and differs from midnight', () {
    final lumen = UiTheme.workspaceDark('lumen');
    final midnight = UiTheme.workspaceDark('midnight');
    expect(lumen.brightness, Brightness.dark);
    expect(lumen.scaffoldBackgroundColor.computeLuminance(), lessThan(0.02));
    final primary = lumen.colorScheme.primary;
    expect(primary.g, greaterThan(primary.r));
    expect(primary.g, greaterThan(primary.b));
    expect(
      lumen.scaffoldBackgroundColor,
      isNot(midnight.scaffoldBackgroundColor),
    );
    expect(
      midnight.scaffoldBackgroundColor,
      UiTheme.workspace(Brightness.dark).scaffoldBackgroundColor,
    );
  });
}
