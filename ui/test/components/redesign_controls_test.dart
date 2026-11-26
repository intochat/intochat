import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  const c = UiColors.intochatLight;

  Future<void> host(WidgetTester tester, Widget child) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(body: Center(child: child)),
      ),
    );
    await tester.pump();
  }

  BoxDecoration decorationIn(WidgetTester tester, Finder of) {
    final box = tester.widget<DecoratedBox>(
      find.descendant(of: of, matching: find.byType(DecoratedBox)).first,
    );
    return box.decoration as BoxDecoration;
  }

  testWidgets('primary button: 36 high, radius 10, accent, 13/600', (
    tester,
  ) async {
    await host(tester, UiKitButton(label: 'Save', onPressed: () {}));
    expect(tester.getSize(find.byType(UiKitButton)).height, 36);
    final d = decorationIn(tester, find.byType(UiKitButton));
    expect(d.borderRadius, BorderRadius.circular(10));
    expect(d.color, c.accent);
    final text = tester.widget<Text>(find.text('Save'));
    expect(text.style?.fontSize, 13);
    expect(text.style?.fontWeight, FontWeight.w600);
    expect(text.style?.color, c.onAccent);
  });

  testWidgets('compact secondary button: 30 high, radius 10, inputBorder', (
    tester,
  ) async {
    await host(
      tester,
      UiKitButton(
        label: 'More',
        onPressed: () {},
        style: UiKitButtonStyle.secondary,
        compact: true,
      ),
    );
    expect(tester.getSize(find.byType(UiKitButton)).height, 30);
    final d = decorationIn(tester, find.byType(UiKitButton));
    expect(d.borderRadius, BorderRadius.circular(10));
    expect((d.border as Border).top.color, c.inputBorder);
    expect(d.color, c.surface);
    expect(tester.widget<Text>(find.text('More')).style?.fontSize, 13);
  });

  testWidgets('danger button is outlined: error border, strong text', (
    tester,
  ) async {
    await host(
      tester,
      UiKitButton(
        label: 'Delete',
        onPressed: () {},
        style: UiKitButtonStyle.danger,
      ),
    );
    final d = decorationIn(tester, find.byType(UiKitButton));
    expect(d.color, Colors.transparent);
    expect((d.border as Border).top.color, c.error);
    expect(tester.widget<Text>(find.text('Delete')).style?.color, c.strong);
  });

  testWidgets('disabled button has opacity 0.45', (tester) async {
    await host(tester, const UiKitButton(label: 'Off'));
    final opacity = tester.widget<Opacity>(
      find.descendant(
        of: find.byType(UiKitButton),
        matching: find.byType(Opacity),
      ),
    );
    expect(opacity.opacity, 0.45);
  });

  testWidgets('button style order mirrors the C# enum', (tester) async {
    expect(UiKitButtonStyle.values.map((s) => s.name), [
      'primary',
      'secondary',
      'ghost',
      'danger',
      'link',
    ]);
  });

  testWidgets('field: 36 high, radius 12; focus draws accent + 3px ring', (
    tester,
  ) async {
    await host(
      tester,
      const SizedBox(
        width: 300,
        child: UiKitField(label: 'Email', error: 'Required'),
      ),
    );
    final box = find.byKey(const ValueKey('ui_kit_field_box'));
    expect(tester.getSize(box).height, 36);
    var d = mixBox(tester, box).decoration as BoxDecoration;
    expect(d.borderRadius, BorderRadius.circular(12));
    expect((d.border as Border).top.color, c.error);
    expect(find.text('Required'), findsOneWidget);
    expect(find.byKey(const ValueKey('ui_kit_field_ring')), findsNothing);

    await host(
      tester,
      const SizedBox(width: 300, child: UiKitField(label: 'Name')),
    );
    await tester.tap(find.byType(TextField));
    await tester.pumpAndSettle();
    d = mixBox(tester, box).decoration as BoxDecoration;
    expect((d.border as Border).top.color, c.accent);
    final ring =
        mixBox(
              tester,
              find.byKey(const ValueKey('ui_kit_field_ring')),
            ).decoration
            as BoxDecoration;
    expect((ring.border as Border).top.width, 3);
    expect((ring.border as Border).top.color, c.focus);
  });

  testWidgets('toggle track is 40x22', (tester) async {
    await host(
      tester,
      SizedBox(
        width: 300,
        child: UiMixToggle(label: 'Dark', value: true, onChanged: (_) {}),
      ),
    );
    expect(
      tester.getSize(find.byKey(const ValueKey('ui_toggle_track'))),
      const Size(40, 22),
    );
  });

  testWidgets('select trigger is 48 high with radius 12', (tester) async {
    await host(
      tester,
      UiKitSelect<String>(
        hint: 'Pick',
        items: const [UiKitSelectItem('a', 'Alpha')],
        value: 'a',
        onChanged: (_) {},
      ),
    );
    final box = find.byKey(const ValueKey('ui_kit_select_box'));
    expect(tester.getSize(box).height, 48);
    final d = mixBox(tester, box).decoration as BoxDecoration;
    expect(d.borderRadius, BorderRadius.circular(12));
  });

  testWidgets('field is named for screen readers above and folded', (
    tester,
  ) async {
    final handle = tester.ensureSemantics();
    await host(
      tester,
      const SizedBox(width: 300, child: UiKitField(label: 'Email')),
    );
    expect(find.bySemanticsLabel(RegExp('Email')), findsOneWidget);
    expect(
      tester.getSemantics(find.byType(TextField)).label,
      contains('Email'),
    );
    await host(
      tester,
      const SizedBox(
        width: 300,
        child: UiKitField(label: '', placeholder: 'Message'),
      ),
    );
    expect(
      tester.getSemantics(find.byType(TextField)).label,
      contains('Message'),
    );
    handle.dispose();
  });
}

/// Mix `Box` renders its style through the first [Container] under its key.
Container mixBox(WidgetTester tester, Finder box) => tester.widget<Container>(
  find.descendant(of: box, matching: find.byType(Container)).first,
);
