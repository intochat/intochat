import 'package:flutter/rendering.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  testWidgets(
    'large menus search labels and submit the visible enabled choice',
    (tester) async {
      String? selected;
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: UiKitSelect<String>(
              value: null,
              hint: 'Open an app',
              items: [
                for (var i = 0; i < 9; i++) UiKitSelectItem('app-$i', 'App $i'),
                const UiKitSelectItem('customer', 'CustomerExample'),
              ],
              onChanged: (value) => selected = value,
            ),
          ),
        ),
      );
      await tester.tap(find.text('Open an app'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField), 'CUSTOMER');
      await tester.pumpAndSettle();
      expect(find.text('CustomerExample'), findsOneWidget);
      expect(find.text('App 0'), findsNothing);
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(selected, 'customer');
      expect(find.byType(TextField), findsNothing);
      await tester.tap(find.text('Open an app'));
      await tester.pumpAndSettle();
      expect(find.text('App 0'), findsOneWidget);
      await tester.enterText(find.byType(TextField), 'missing');
      await tester.pumpAndSettle();
      expect(find.text('No matching options'), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('small menus align labels and preserve disabled options', (
    tester,
  ) async {
    String? selected;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: UiKitSelect<String>(
            value: null,
            hint: 'Choose',
            items: const [
              UiKitSelectItem('a', 'Short'),
              UiKitSelectItem('b', 'A much longer label', enabled: false),
            ],
            onChanged: (value) => selected = value,
          ),
        ),
      ),
    );
    await tester.tap(find.text('Choose'));
    await tester.pumpAndSettle();
    expect(find.byType(TextField), findsNothing);
    expect(
      tester.getTopLeft(find.text('Short')).dx,
      tester.getTopLeft(find.text('A much longer label')).dx,
    );
    await tester.tap(find.text('A much longer label'));
    expect(selected, isNull);
    await tester.tap(find.text('Short'));
    await tester.pumpAndSettle();
    expect(selected, 'a');
    expect(tester.takeException(), isNull);
  });

  testWidgets(
    'duplicate labels wrap while preserving full text and alignment',
    (tester) async {
      const names = [
        'Customer Researcher (customer-researcher)',
        'Customer Researcher (customer-researcher-copy)',
      ];
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: UiKitSelect<String>(
              value: null,
              hint: 'Choose',
              items: [for (final name in names) UiKitSelectItem(name, name)],
              onChanged: (_) {},
            ),
          ),
        ),
      );
      await tester.tap(find.text('Choose'));
      await tester.pumpAndSettle();
      for (final name in names) {
        final paragraph = tester.renderObject<RenderParagraph>(find.text(name));
        expect(paragraph.text.toPlainText(), name);
        expect(paragraph.size.height, greaterThan(20));
      }
      expect(
        tester.getTopLeft(find.text(names[0])).dx,
        tester.getTopLeft(find.text(names[1])).dx,
      );
      expect(tester.takeException(), isNull);
    },
  );
}
