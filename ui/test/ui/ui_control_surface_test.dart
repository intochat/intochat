import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/src/theme/ui_theme.dart';
import 'package:intochat_os_flutter_shell/src/ui/ui_control_surface.dart';

void main() {
  testWidgets(
    'UiControlSurface runs local hover/press transitions and gates neuron signals by UiEventMask',
    (tester) async {
      final emitted = <String>[];

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: Center(
              child: UiControlSurface(
                eventMask: UiEventMask.hover | UiEventMask.press,
                motion: UiMotionPreset.snappy,
                onEmit: (method, _) async => emitted.add(method),
                builder: (_, state) => SizedBox(
                  key: const Key('target'),
                  width: 120,
                  height: 44,
                  child: Text(
                    'h:${state.isHovered},p:${state.isPressed}',
                  ),
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
      expect(emitted, contains('OnPointerEnter'));

      await gesture.down(tester.getCenter(find.byKey(const Key('target'))));
      await tester.pumpAndSettle();
      expect(find.text('h:true,p:true'), findsOneWidget);
      expect(emitted, contains('OnPointerDown'));

      await gesture.up();
      await tester.pumpAndSettle();
      expect(find.text('h:true,p:false'), findsOneWidget);
      expect(emitted, contains('OnPointerUp'));

      await gesture.moveTo(Offset.zero);
      await tester.pumpAndSettle();
      expect(find.text('h:false,p:false'), findsOneWidget);
      expect(emitted, contains('OnPointerLeave'));
    },
  );

  testWidgets(
    'UiControlSurface emits zero hover/press RPCs when UiEventMask.none is set',
    (tester) async {
      final emitted = <String>[];

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: Center(
              child: UiControlSurface(
                eventMask: UiEventMask.none,
                onEmit: (method, _) async => emitted.add(method),
                builder: (_, state) => SizedBox(
                  key: const Key('quiet'),
                  width: 120,
                  height: 44,
                  child: Text('h:${state.isHovered}'),
                ),
              ),
            ),
          ),
        ),
      );

      final gesture = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await gesture.addPointer(location: Offset.zero);
      addTearDown(gesture.removePointer);

      await gesture.moveTo(tester.getCenter(find.byKey(const Key('quiet'))));
      await tester.pumpAndSettle();
      // Local hover animation still updates at 0ms latency!
      expect(find.text('h:true'), findsOneWidget);
      // Zero network/neuron noise!
      expect(emitted, isEmpty);
    },
  );
}
