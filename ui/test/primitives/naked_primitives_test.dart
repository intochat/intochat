import 'dart:ui' show Tristate;

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:naked_ui/naked_ui.dart';

Widget _host(Widget child) => MaterialApp(
  theme: UiTheme.intochat(Brightness.light),
  home: Scaffold(body: Center(child: child)),
);

Container _mixBox(WidgetTester tester, Finder box) => tester.widget<Container>(
  find.descendant(of: box, matching: find.byType(Container)).first,
);

void main() {
  const c = UiColors.intochatLight;

  group('UiMixTabs (NakedTabs)', () {
    testWidgets(
      'selects tabs on tap, skips disabled tabs, and navigates with arrows',
      (tester) async {
        var current = 0;
        await tester.pumpWidget(
          _host(
            StatefulBuilder(
              builder: (context, setState) => UiMixTabs(
                labels: const ['First', 'Disabled', 'Third'],
                index: current,
                disabled: const {1},
                onChanged: (next) => setState(() => current = next),
              ),
            ),
          ),
        );

        expect(find.byType(NakedTabs), findsOneWidget);
        expect(find.byType(NakedTabBar), findsOneWidget);
        expect(find.byType(NakedTab), findsNWidgets(3));

        // Tapping disabled tab does nothing.
        await tester.tap(find.text('Disabled'));
        await tester.pumpAndSettle();
        expect(current, 0);

        // Focus first tab and arrowRight skips disabled tab 1 to select tab 2.
        final firstTabFocus = Focus.of(
          tester.element(find.byKey(const ValueKey('ui_mix_tab_0'))),
        );
        firstTabFocus.requestFocus();
        await tester.pump();
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
        await tester.pumpAndSettle();
        expect(current, 2);

        // arrowLeft skips disabled tab 1 back to tab 0.
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
        await tester.pumpAndSettle();
        expect(current, 0);
      },
    );

    testWidgets('reflects hover, focus, and tab semantics', (tester) async {
      final handle = tester.ensureSemantics();
      await tester.pumpWidget(
        _host(
          UiMixTabs(
            labels: const ['Alpha', 'Beta'],
            index: 0,
            onChanged: (_) {},
          ),
        ),
      );

      final tab1Box = find.byKey(const ValueKey('ui_mix_tab_1'));
      final gesture = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await gesture.addPointer(location: Offset.zero);
      addTearDown(gesture.removePointer);
      await gesture.moveTo(tester.getCenter(tab1Box));
      await tester.pump();

      final hoveredDec = _mixBox(tester, tab1Box).decoration as BoxDecoration;
      expect(hoveredDec.color, c.recessed.withValues(alpha: 0.6));

      final tab0Semantics = tester.getSemantics(find.byType(NakedTab).first);
      expect(tab0Semantics.flagsCollection.isSelected, Tristate.isTrue);
      expect(tab0Semantics.flagsCollection.isEnabled, Tristate.isTrue);

      handle.dispose();
    });
  });

  group('UiMixButton (NakedButton.builder)', () {
    testWidgets(
      'updates hover, press, focus, and disabled states without duplicate button Semantics',
      (tester) async {
        var taps = 0;
        final focusNode = FocusNode();
        addTearDown(focusNode.dispose);

        await tester.pumpWidget(
          _host(
            UiMixButton.preset(
              label: 'Action',
              focusNode: focusNode,
              semanticsId: 'btn:action',
              onPressed: () => taps++,
            ),
          ),
        );

        // UiMixButton delegates button Semantics solely to NakedButton (no
        // extra button:true wrapper on semanticsId or around NakedButton).
        final idSemantics = tester
            .widgetList<Semantics>(
              find.descendant(
                of: find.byType(UiMixButton),
                matching: find.byType(Semantics),
              ),
            )
            .where((s) => s.properties.identifier == 'btn:action')
            .single;
        expect(idSemantics.properties.button, isNull);
        final nakedButtonSemantics = tester
            .widgetList<Semantics>(
              find.descendant(
                of: find.byType(NakedButton),
                matching: find.byType(Semantics),
              ),
            )
            .where((s) => s.properties.button == true)
            .toList();
        expect(nakedButtonSemantics, hasLength(1));

        // Hover changes primary background from accent to accentHover.
        final gesture = await tester.createGesture(
          kind: PointerDeviceKind.mouse,
        );
        await gesture.addPointer(location: Offset.zero);
        addTearDown(gesture.removePointer);
        await gesture.moveTo(tester.getCenter(find.byType(UiMixButton)));
        await tester.pumpAndSettle();

        final hoveredBox = tester.widget<DecoratedBox>(
          find
              .descendant(
                of: find.byType(UiMixButton),
                matching: find.byType(DecoratedBox),
              )
              .first,
        );
        expect((hoveredBox.decoration as BoxDecoration).color, c.accentHover);

        // Press applies 1px downward translate and invokes callback on release.
        final pressGesture = await tester.startGesture(
          tester.getCenter(find.byType(UiMixButton)),
        );
        await tester.pump(const Duration(milliseconds: 120));
        final pressedTransform = tester.widget<Transform>(
          find
              .descendant(
                of: find.byType(SelectionRing),
                matching: find.byType(Transform),
              )
              .first,
        );
        expect(pressedTransform.transform.getTranslation().y, 1.0);
        await pressGesture.up();
        await tester.pumpAndSettle();
        expect(taps, 1);

        // Focus activates focus border.
        focusNode.requestFocus();
        await tester.pumpAndSettle();
        final focusedBox = tester.widget<DecoratedBox>(
          find
              .descendant(
                of: find.byType(UiMixButton),
                matching: find.byType(DecoratedBox),
              )
              .first,
        );
        expect(
          ((focusedBox.decoration as BoxDecoration).border as Border?)
              ?.top
              .color,
          c.accent,
        );
      },
    );

    testWidgets('disabled UiMixButton ignores taps and has 0.45 opacity', (
      tester,
    ) async {
      var taps = 0;
      await tester.pumpWidget(
        _host(
          UiMixButton.preset(
            label: 'Disabled',
            enabled: false,
            onPressed: () => taps++,
          ),
        ),
      );

      await tester.tap(find.text('Disabled'));
      await tester.pumpAndSettle();
      expect(taps, 0);
      final opacity = tester.widget<Opacity>(
        find.descendant(
          of: find.byType(UiMixButton),
          matching: find.byType(Opacity),
        ),
      );
      expect(opacity.opacity, 0.45);
    });
  });

  group('UiMixExpander (header-scoped NakedButton.builder)', () {
    testWidgets(
      'tapping header toggles expander while expanded body stays outside NakedButton',
      (tester) async {
        var expanded = false;
        var childTaps = 0;
        const bodyKey = ValueKey('expander_body_content');

        await tester.pumpWidget(
          _host(
            SizedBox(
              width: 320,
              child: StatefulBuilder(
                builder: (context, setState) => UiMixExpander(
                  header: 'Advanced',
                  isExpanded: expanded,
                  onExpandedChanged: (next) => setState(() => expanded = next),
                  body: Container(
                    key: bodyKey,
                    child: UiMixButton.preset(
                      label: 'Inner Action',
                      onPressed: () => childTaps++,
                    ),
                  ),
                ),
              ),
            ),
          ),
        );

        expect(expanded, isFalse);
        expect(find.byKey(bodyKey), findsNothing);

        // Tapping header expands UiMixExpander.
        await tester.tap(find.text('Advanced'));
        await tester.pumpAndSettle();
        expect(expanded, isTrue);
        expect(find.byKey(bodyKey), findsOneWidget);

        // Expanded body is strictly outside the header's NakedButton.
        expect(
          find.ancestor(
            of: find.byKey(bodyKey),
            matching: find.byType(NakedButton),
          ),
          findsNothing,
        );
        final headerButton = find
            .ancestor(
              of: find.text('Advanced'),
              matching: find.byType(NakedButton),
            )
            .first;
        expect(
          find.descendant(of: headerButton, matching: find.byKey(bodyKey)),
          findsNothing,
        );

        // Tapping interactive child invokes child without collapsing expander.
        await tester.tap(find.text('Inner Action'));
        await tester.pumpAndSettle();
        expect(childTaps, 1);
        expect(expanded, isTrue);

        // Tapping header again collapses UiMixExpander.
        await tester.tap(find.text('Advanced'));
        await tester.pumpAndSettle();
        expect(expanded, isFalse);
      },
    );
  });
}
