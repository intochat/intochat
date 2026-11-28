import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  Future<void> pumpSash(
    WidgetTester tester,
    Widget sash, {
    bool disableAnimations = false,
  }) async {
    await tester.pumpWidget(
      MediaQuery(
        data: MediaQueryData(
          size: const Size(800, 600),
          disableAnimations: disableAnimations,
        ),
        child: MaterialApp(
          theme: UiTheme.light('intochat'),
          home: Scaffold(
            body: SizedBox(
              width: 400,
              height: 300,
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  const SizedBox(width: 120),
                  sash,
                  const Expanded(child: SizedBox.expand()),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  group('UiResizableSash', () {
    testWidgets('emits horizontal drag deltas and double-tap reset', (
      tester,
    ) async {
      final deltas = <double>[];
      var resets = 0;

      await pumpSash(
        tester,
        UiResizableSash(
          key: const ValueKey('test_sash'),
          label: 'Resize sidebar',
          semanticsIdentifier: 'key:test-sash',
          onDelta: deltas.add,
          onReset: () => resets++,
        ),
      );

      final sashFinder = find.byKey(const ValueKey('test_sash'));
      await tester.drag(sashFinder, const Offset(60, 0));
      await tester.pumpAndSettle();

      final totalDelta = deltas.fold<double>(0, (sum, d) => sum + d);
      expect(totalDelta, greaterThan(20));

      await tester.tap(sashFinder);
      await tester.pump(const Duration(milliseconds: 50));
      await tester.tap(sashFinder);
      await tester.pumpAndSettle();

      expect(resets, 1);
    });

    testWidgets(
      'supports keyboard ArrowLeft/ArrowRight nudge and ActivateIntent reset',
      (tester) async {
        final deltas = <double>[];
        var resets = 0;

        await pumpSash(
          tester,
          UiResizableSash(
            key: const ValueKey('kb_sash'),
            label: 'Resize inspector',
            keyboardStep: 20,
            onDelta: deltas.add,
            onReset: () => resets++,
          ),
        );

        final detectorChild = find.descendant(
          of: find.byKey(const ValueKey('kb_sash')),
          matching: find.byType(GestureDetector),
        );
        Focus.of(tester.element(detectorChild)).requestFocus();
        await tester.pump();

        await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
        await tester.pump();
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
        await tester.pump();
        expect(deltas, [20, -20]);

        await tester.sendKeyEvent(LogicalKeyboardKey.enter);
        await tester.pump();
        expect(resets, 1);
      },
    );

    testWidgets('respects reduced-motion and exposes slider semantics', (
      tester,
    ) async {
      final semanticsHandle = tester.ensureSemantics();
      try {
        await pumpSash(
          tester,
          UiResizableSash(
            key: const ValueKey('a11y_sash'),
            label: 'Resize sidebar',
            semanticsIdentifier: 'key:shell-sash-sidebar',
            semanticsHint: 'Drag or use arrow keys',
            semanticsValue: '248px',
            hitWidth: 12,
            onDelta: (_) {},
          ),
          disableAnimations: true,
        );

        final sashFinder = find.byKey(const ValueKey('a11y_sash'));
        expect(tester.getSize(sashFinder).width, 12);

        final pill = tester.widget<AnimatedContainer>(
          find.descendant(
            of: sashFinder,
            matching: find.byType(AnimatedContainer),
          ),
        );
        expect(pill.duration, Duration.zero);

        final data = tester.getSemantics(sashFinder).getSemanticsData();
        expect(data.identifier, 'key:shell-sash-sidebar');
        expect(data.label, 'Resize sidebar');
        expect(data.hint, 'Drag or use arrow keys');
        expect(data.value, '248px');
        expect(data.flagsCollection.isSlider, isTrue);
      } finally {
        semanticsHandle.dispose();
      }
    });

    testWidgets('ignores pointer and keyboard interaction when disabled', (
      tester,
    ) async {
      var deltaCalled = false;
      var resetCalled = false;

      await pumpSash(
        tester,
        UiResizableSash(
          key: const ValueKey('disabled_sash'),
          label: 'Resize sidebar',
          enabled: false,
          onDelta: (_) => deltaCalled = true,
          onReset: () => resetCalled = true,
        ),
      );

      final sashFinder = find.byKey(const ValueKey('disabled_sash'));
      final detector = tester.widget<FocusableActionDetector>(
        find.descendant(
          of: sashFinder,
          matching: find.byType(FocusableActionDetector),
        ),
      );
      expect(detector.enabled, isFalse);
      expect(detector.mouseCursor, MouseCursor.defer);

      await tester.drag(sashFinder, const Offset(40, 0));
      await tester.pump();
      await tester.tap(sashFinder);
      await tester.pump(const Duration(milliseconds: 50));
      await tester.tap(sashFinder);
      await tester.pumpAndSettle();

      expect(deltaCalled, isFalse);
      expect(resetCalled, isFalse);
    });
  });
}
