import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/src/theme/ui_theme.dart';
import 'package:intochat_os_flutter_shell/src/widgets/neuron/ui_control_surface.dart';

void main() {
  testWidgets(
    'UiControlSurface runs local hover/press transitions and reports them to local callbacks',
    (tester) async {
      final emitted = <String>[];

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: Center(
              child: UiControlSurface(
                motion: UiMotionPreset.snappy,
                onPointerEnter: () => emitted.add('enter'),
                onPointerLeave: () => emitted.add('leave'),
                onPointerDown: () => emitted.add('down'),
                onPointerUp: () => emitted.add('up'),
                builder: (_, state) => SizedBox(
                  key: const Key('target'),
                  width: 120,
                  height: 44,
                  child: Text('h:${state.isHovered},p:${state.isPressed}'),
                ),
              ),
            ),
          ),
        ),
      );

      final gesture = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await gesture.addPointer(location: Offset.zero);
      addTearDown(gesture.removePointer);

      await gesture.moveTo(tester.getCenter(find.byKey(const Key('target'))));
      await tester.pumpAndSettle();
      expect(find.text('h:true,p:false'), findsOneWidget);
      expect(emitted, contains('enter'));

      await gesture.down(tester.getCenter(find.byKey(const Key('target'))));
      await tester.pumpAndSettle();
      expect(find.text('h:true,p:true'), findsOneWidget);
      expect(emitted, contains('down'));

      await gesture.up();
      await tester.pumpAndSettle();
      expect(find.text('h:true,p:false'), findsOneWidget);
      expect(emitted, contains('up'));

      await gesture.moveTo(Offset.zero);
      await tester.pumpAndSettle();
      expect(find.text('h:false,p:false'), findsOneWidget);
      expect(emitted, contains('leave'));
    },
  );
}
