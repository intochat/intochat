@Tags(['golden'])
library;

import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import '../composition/desktop_shape.dart';

void main() {
  if (!Platform.isWindows) {
    test(
      'desktop shape goldens',
      () {},
      skip: 'Goldens are Windows-rendered; Windows-local regression net only.',
    );
    return;
  }
  final cases = <String, ThemeData Function()>{
    'intochat_light': () => UiTheme.light('intochat'),
    'intochat_dark': () => UiTheme.dark('intochat'),
  };
  for (final entry in cases.entries) {
    testWidgets('desktop shape ${entry.key}', (tester) async {
      tester.view.physicalSize = const Size(1280, 800);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      debugDisableShadows = false;
      try {
        await pumpShape(tester, desktopShape(), theme: entry.value());
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        await expectLater(
          find.byType(NeuronView),
          matchesGoldenFile('desktop_shape_${entry.key}.png'),
        );
      } finally {
        debugDisableShadows = true;
      }
    });
  }
}
