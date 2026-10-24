import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'package:intochat_os_ui/src/components/kit/ui_kit_tabs.dart';
import 'package:intochat_os_ui/src/composition/value_nodes/view_menu.dart';

import 'desktop_shape.dart';

Finder _icon(String label) => find.byWidgetPredicate(
  (widget) => widget is ViewIconButton && widget.label == label,
);

void main() {
  const wide = Size(1440, 900);
  const narrow = Size(390, 844);

  testWidgets('desktop_window_has_header_tabs_and_bounded_content', (
    tester,
  ) async {
    await pumpShape(tester, desktopShape(), size: wide);
    expect(tester.takeException(), isNull);
    final strips = find.byType(UiKitTabs);
    expect(strips, findsNWidgets(2));
    for (var i = 0; i < 2; i++) {
      final tabs = tester.getRect(strips.at(i));
      // Windows start at the top: the header is the 40 px above the strip.
      expect(tabs.top, 40, reason: 'window $i header');
      expect(tabs.height, 38, reason: 'window $i tabs');
    }
    // Content fills from the strip's bottom to the 68 px dock.
    for (final text in ['Files content', 'Transcript']) {
      final content = tester.getRect(find.text(text));
      expect(content.top, greaterThanOrEqualTo(78), reason: text);
      expect(content.bottom, lessThanOrEqualTo(900 - 68), reason: text);
    }
    expect(find.text('Tab 3'), findsOneWidget);
  });

  testWidgets('desktop_chrome_icon_buttons_fit_32_slots', (tester) async {
    await pumpShape(tester, desktopShape(), size: wide);
    final strip = tester.getRect(find.byType(UiKitTabs).first);
    var chrome = 0;
    for (final label in ['New tab', 'Add to favourites', 'Pin', 'Close tab']) {
      for (final button in _icon(label).evaluate()) {
        final rect = tester.getRect(find.byWidget(button.widget));
        if (rect.bottom > strip.top) continue; // the dock's own icon button
        chrome++;
        expect(rect.width, 32, reason: label);
        expect(rect.height, lessThanOrEqualTo(40), reason: label);
      }
    }
    expect(chrome, 7); // Ino has no pin; Files has all four.
  });

  testWidgets('desktop_favourites_hidden_when_empty', (tester) async {
    await pumpShape(tester, desktopShape(), size: wide);
    expect(_icon('Notes'), findsNothing);
    await tester.pumpWidget(const SizedBox());
    await pumpShape(tester, desktopShape(favourites: 3), size: wide);
    expect(_icon('Notes'), findsNWidgets(2));
    expect(_icon('Report'), findsOneWidget);
  });

  testWidgets(
    'desktop_narrow_390_shows_one_window_and_favourites_menu_without_overflow',
    (tester) async {
      await pumpShape(
        tester,
        desktopShape(narrow: true, favourites: 3),
        size: narrow,
      );
      expect(tester.takeException(), isNull);
      expect(find.byType(UiKitTabs), findsOneWidget);
      expect(find.text('Transcript'), findsNothing);
      expect(find.text('Files content'), findsOneWidget);
      expect(find.byType(ViewMenu), findsOneWidget);
      expect(_icon('Notes'), findsNothing);
    },
  );

  testWidgets('desktop_refusal_toast_shows_its_text', (tester) async {
    await pumpShape(
      tester,
      desktopShape(toast: 'A window holds at most 8 tabs.'),
      size: wide,
    );
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.text('A window holds at most 8 tabs.'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}
