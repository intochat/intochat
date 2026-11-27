import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  const c = UiColors.intochatLight;
  const boxKey = ValueKey('ui_kit_select_box');

  Future<void> host(WidgetTester tester, Widget child) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(width: 600, child: child),
          ),
        ),
      ),
    );
    await tester.pump();
  }

  UiMixSelect<String> select({
    ValueChanged<String>? onChanged,
    String? value,
  }) => UiMixSelect<String>(
    value: value,
    label: 'Model',
    hint: 'Pick one',
    onChanged: onChanged,
    items: const [
      UiMixSelectItem('a', 'Alpha'),
      UiMixSelectItem('b', 'Beta', enabled: false, detail: 'Needs a key'),
      UiMixSelectItem('c', 'Gamma'),
    ],
  );

  testWidgets('fills a 600-wide parent at 48 high with radius 12', (
    tester,
  ) async {
    await host(tester, select(onChanged: (_) {}, value: 'a'));
    expect(tester.getSize(find.byKey(boxKey)), const Size(600, 48));
    final d = mixBox(tester, find.byKey(boxKey)).decoration as BoxDecoration;
    expect(d.borderRadius, BorderRadius.circular(12));
    expect((d.border as Border).top.color, c.inputBorder);
    expect(find.text('Alpha'), findsOneWidget);
  });

  testWidgets('shows the hint with no value', (tester) async {
    await host(tester, select(onChanged: (_) {}));
    expect(find.text('Pick one'), findsOneWidget);
  });

  testWidgets('disabled items cannot be chosen', (tester) async {
    String? got;
    await host(tester, select(onChanged: (v) => got = v, value: 'a'));
    await tester.tap(find.byKey(boxKey));
    await tester.pumpAndSettle();
    expect(find.text('Needs a key'), findsOneWidget);
    await tester.tap(find.text('Beta'));
    await tester.pumpAndSettle();
    expect(got, isNull);
    await tester.tap(find.text('Gamma'));
    await tester.pumpAndSettle();
    expect(got, 'c');
  });

  testWidgets('focus draws a 3 px focus ring', (tester) async {
    await host(tester, select(onChanged: (_) {}, value: 'a'));
    var d = mixBox(tester, find.byKey(boxKey)).decoration as BoxDecoration;
    expect(
      d.boxShadow?.any((s) => s.color == c.focus && s.spreadRadius == 3),
      isNot(true),
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.tab);
    await tester.pumpAndSettle();
    d = mixBox(tester, find.byKey(boxKey)).decoration as BoxDecoration;
    expect((d.border as Border).top.color, c.accent);
    expect(
      d.boxShadow?.any((s) => s.color == c.focus && s.spreadRadius == 3),
      isTrue,
    );
  });

  testWidgets('keyboard Space opens overlay and Escape closes it', (
    tester,
  ) async {
    String? got;
    await host(tester, select(onChanged: (v) => got = v, value: 'a'));
    await tester.sendKeyEvent(LogicalKeyboardKey.tab);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.space);
    await tester.pumpAndSettle();
    expect(find.text('Gamma'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pumpAndSettle();
    expect(find.text('Gamma'), findsNothing);
    expect(got, isNull);
  });
}

/// Mix `Box` renders its style through the first [Container] under its key.
Container mixBox(WidgetTester tester, Finder box) => tester.widget<Container>(
  find.descendant(of: box, matching: find.byType(Container)).first,
);
