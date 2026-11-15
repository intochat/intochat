import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:intochat_os_flutter_shell/src/composition/value_nodes/view_menu.dart';

Future<void> _pump(WidgetTester tester, Widget child) => tester.pumpWidget(
  MaterialApp(
    home: Material(
      child: Align(alignment: Alignment.topLeft, child: child),
    ),
  ),
);

void main() {
  for (final key in [LogicalKeyboardKey.enter, LogicalKeyboardKey.space]) {
    testWidgets('view_menu_reference_closes_on_keyboard_${key.keyLabel}', (
      tester,
    ) async {
      var pressed = 0;
      final item = FocusNode();
      addTearDown(item.dispose);
      await _pump(
        tester,
        ViewMenu(
          node: const {
            'text': 'More',
            'children': [
              {'kind': 5, 'text': 'Ref'},
            ],
          },
          action: (_) => null,
          reference: (_) => TextButton(
            focusNode: item,
            onPressed: () => pressed++,
            child: const Text('Reference item'),
          ),
        ),
      );
      await tester.tap(find.text('More'));
      await tester.pumpAndSettle();
      expect(find.text('Reference item'), findsOneWidget);
      item.requestFocus();
      await tester.pump();
      await tester.sendKeyEvent(key);
      await tester.pumpAndSettle();
      expect(pressed, 1);
      expect(find.text('Reference item'), findsNothing);
    });
  }
}
