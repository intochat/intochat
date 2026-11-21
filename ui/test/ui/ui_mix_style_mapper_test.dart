import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/src/ui/mix/ui_mix_style.dart';
import 'package:intochat_os_flutter_shell/src/ui/ui_control_surface.dart';

void main() {
  testWidgets(
    'UiMixButton renders exact C# UiStyleRef padding/radius via mix and emits WinForms pointer signals',
    (tester) async {
      final emitted = <String>[];
      var clicks = 0;

      // Simulates C# UiStyleRef serialized over JSON.
      final styleFromCSharp = UiStyleData.fromJson({
        'tone': 'accent',
        'paddingX': 24,
        'paddingY': 14,
        'radius': 12,
        'motion': 'spring',
      });

      expect(styleFromCSharp.paddingX, 24);
      expect(styleFromCSharp.paddingY, 14);
      expect(styleFromCSharp.radius, 12);
      expect(styleFromCSharp.motion, UiMotionPreset.spring);

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: Center(
              child: UiMixButton(
                label: 'Save',
                style: styleFromCSharp,
                eventMask: UiEventMask.all,
                semanticsId: 'key:mix:save',
                onPressed: () => clicks++,
                onEmit: (method, _) async => emitted.add(method),
              ),
            ),
          ),
        ),
      );

      final containerFinder = find.byType(Container).first;
      final container = tester.widget<Container>(containerFinder);
      expect(
        container.padding,
        equals(const EdgeInsets.symmetric(horizontal: 24, vertical: 14)),
      );

      final gesture = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await gesture.addPointer(location: Offset.zero);
      addTearDown(gesture.removePointer);

      final center = tester.getCenter(find.text('Save'));
      await gesture.moveTo(center);
      await tester.pumpAndSettle();
      expect(emitted, contains('OnPointerEnter'));

      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();
      expect(clicks, 1);
      expect(emitted, contains('OnPointerDown'));
      expect(emitted, contains('OnPointerUp'));
    },
  );
}
