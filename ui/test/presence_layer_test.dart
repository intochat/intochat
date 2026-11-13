import 'dart:async';

import 'package:flutter/material.dart' hide Focus;
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/desktop/presence_layer.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

Presence presence(PresenceState state, {Focus? focus, int revision = 1}) =>
    Presence(state: state, focus: focus, revision: revision);

final class Harness {
  final controller = StreamController<Presence>.broadcast();
  final registry = FocusRegistry();
  final actions = <FocusAction>[];
  var opened = 0;
  final targetKey = GlobalKey();

  Widget build({
    bool enabled = true,
    bool reducedMotion = false,
    String? target,
  }) {
    if (target != null) registry.register(target, targetKey);
    return MaterialApp(
      home: MediaQuery(
        data: MediaQueryData(
          size: const Size(800, 600),
          disableAnimations: reducedMotion,
        ),
        child: Stack(
          children: [
            Positioned(
              left: 300,
              top: 200,
              width: 200,
              height: 100,
              child: SizedBox(key: targetKey),
            ),
            Positioned.fill(
              child: PresenceLayer(
                presence: controller.stream,
                enabled: enabled,
                registry: registry,
                onAction: actions.add,
                onOpenIno: () => opened++,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

PresenceLayerState layer(WidgetTester tester) =>
    tester.state<PresenceLayerState>(find.byType(PresenceLayer));

Future<void> emit(WidgetTester tester, Harness h, Presence p) async {
  h.controller.add(p);
  await tester.pump();
  await tester.pump();
}

const _target = 'button:apps/files/save';
const _focus = Focus(
  target: _target,
  tone: FocusTone.warning,
  reason: 'Save the file',
  action: FocusAction(kind: 'open', argument: 'files'),
);

void main() {
  testWidgets('layerAbsentWhenSmartOff', (tester) async {
    final h = Harness();
    await tester.pumpWidget(h.build(enabled: false));
    await tester.pump(const Duration(milliseconds: 50));
    expect(find.byKey(const ValueKey('presence-sphere')), findsNothing);
  });

  testWidgets('focusDrawsRingAtResolvedRect', (tester) async {
    final h = Harness();
    await tester.pumpWidget(h.build(target: _target));
    await tester.pump(const Duration(milliseconds: 16));
    await emit(tester, h, presence(PresenceState.working, focus: _focus));
    expect(layer(tester).ringRect, const Rect.fromLTWH(300, 200, 200, 100));
  });

  testWidgets('unresolvedTargetFallsBackToWindowThenToast', (tester) async {
    final h = Harness();
    // The component is not registered, but its app's window is.
    await tester.pumpWidget(h.build(target: 'window:files'));
    await tester.pump(const Duration(milliseconds: 16));
    await emit(tester, h, presence(PresenceState.working, focus: _focus));
    expect(layer(tester).ringRect, const Rect.fromLTWH(300, 200, 200, 100));
    expect(find.byKey(const ValueKey('presence-toast')), findsNothing);

    await emit(
      tester,
      h,
      presence(
        PresenceState.working,
        revision: 2,
        focus: const Focus(
          target: 'window:mail',
          tone: FocusTone.info,
          reason: 'Mail is closed',
        ),
      ),
    );
    expect(layer(tester).ringRect, isNull);
    expect(find.text('Mail is closed'), findsOneWidget);
    await tester.pump(const Duration(seconds: 5));
  });

  testWidgets('flightTakes420msOrCutsUnderReducedMotion', (tester) async {
    final h = Harness();
    await tester.pumpWidget(h.build(target: _target));
    await tester.pump(const Duration(milliseconds: 16));
    final start = layer(tester).sphereCenter!;
    await emit(tester, h, presence(PresenceState.working, focus: _focus));
    final corner = [
      const Offset(300, 200),
      const Offset(500, 200),
      const Offset(300, 300),
      const Offset(500, 300),
    ].reduce((a, b) => (a - start).distance <= (b - start).distance ? a : b);
    await tester.pump(const Duration(milliseconds: 200));
    expect(layer(tester).sphereCenter, isNot(corner));
    await tester.pump(const Duration(milliseconds: 240));
    expect(layer(tester).sphereCenter, corner);

    final r = Harness();
    await tester.pumpWidget(r.build(target: _target, reducedMotion: true));
    await tester.pump(const Duration(milliseconds: 16));
    await emit(tester, r, presence(PresenceState.working, focus: _focus));
    final cut = layer(tester).sphereCenter!;
    expect([
      const Offset(300, 200),
      const Offset(500, 200),
      const Offset(300, 300),
      const Offset(500, 300),
    ], contains(cut));
  });

  testWidgets('sphereTapOpensIno', (tester) async {
    final h = Harness();
    await tester.pumpWidget(h.build());
    await tester.pump(const Duration(milliseconds: 16));
    await emit(tester, h, presence(PresenceState.paused));
    await tester.tap(find.byKey(const ValueKey('presence-sphere')));
    expect(h.opened, 1);
  });

  testWidgets('ringTapRunsAction', (tester) async {
    final h = Harness();
    await tester.pumpWidget(h.build(target: _target));
    await tester.pump(const Duration(milliseconds: 16));
    await emit(tester, h, presence(PresenceState.working, focus: _focus));
    await tester.pump(const Duration(milliseconds: 500));
    await tester.tapAt(const Offset(400, 300));
    expect(h.actions.single.argument, 'files');
  });

  testWidgets('pausedStopsIdleDrift', (tester) async {
    final h = Harness();
    await tester.pumpWidget(h.build());
    await tester.pump(const Duration(milliseconds: 16));
    final before = layer(tester).sphereCenter;
    await tester.pump(const Duration(milliseconds: 500));
    expect(layer(tester).sphereCenter, isNot(before), reason: 'idle drifts');

    await emit(tester, h, presence(PresenceState.paused));
    final paused = layer(tester).sphereCenter;
    await tester.pump(const Duration(seconds: 2));
    expect(layer(tester).sphereCenter, paused);
  });

  testWidgets('idle drift skips points inside windows', (tester) async {
    final h = Harness();
    await tester.pumpWidget(h.build(target: 'window:files'));
    for (var i = 0; i < 60; i++) {
      await tester.pump(const Duration(milliseconds: 100));
      final c = layer(tester).sphereCenter!;
      expect(
        const Rect.fromLTWH(300, 200, 200, 100).inflate(28).contains(c),
        isFalse,
      );
    }
  });
}
