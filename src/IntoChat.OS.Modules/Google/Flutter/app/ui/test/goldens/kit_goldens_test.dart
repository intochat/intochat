import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'kit_sheet.dart';

void main() {
  final cases = <String, ThemeData Function()>{
    'intochat_light': () => UiTheme.light('intochat'),
    'intochat_dark': () => UiTheme.dark('intochat'),
    'midnight': () => UiTheme.dark('midnight'),
    'paper': () => UiTheme.light('paper'),
    'lumen': () => UiTheme.dark('lumen'),
  };
  for (final entry in cases.entries) {
    testWidgets('kit sheet ${entry.key}', (tester) async {
      tester.view.physicalSize = const Size(1280, 900);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      await tester.pumpWidget(
        MaterialApp(
          debugShowCheckedModeBanner: false,
          theme: entry.value(),
          home: const Material(child: KitSheet()),
        ),
      );
      await tester.pumpAndSettle();
      await expectLater(
        find.byType(KitSheet),
        matchesGoldenFile('kit_${entry.key}.png'),
      );
    });
  }
}
