import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  Widget hostConversation(Map<String, dynamic> root) => MaterialApp(
    theme: UiTheme.light('intochat'),
    home: Scaffold(
      body: ViewHost(
        kind: 'view',
        name: 'local/apps/ino/view',
        overrides: ViewHostOverrides(
          knowsApp: (_) => false,
          onAction: (_) async => null,
          load: (_, _) async => PublishedViewPayload.fromJson({
            'generation': 1,
            'view': {'title': 'Ino', 'root': root},
          }),
        ),
      ),
    ),
  );

  testWidgets(
    'typing @ shows mention pills, Esc dismisses, typing resets dismiss',
    (tester) async {
      await tester.pumpWidget(
        hostConversation({
          'kind': 'conversation',
          'key': 'conv',
          'messages': [
            {
              'id': 'm1',
              'role': 'user',
              'text': 'Hi',
              'status': 'completed',
              'sender': {
                'accountId': 'alice',
                'displayName': 'Alice',
                'kind': 'human',
              },
            },
          ],
          'composer': {
            'key': 'composer',
            'onSubmit': {'target': '.', 'signal': 'ino.send'},
            'placeholder': 'Message...',
            'enabled': true,
          },
          'status': 'ready',
        }),
      );
      await tester.pumpAndSettle();

      final field = find.byType(TextField);
      await tester.tap(field);
      await tester.enterText(field, 'Hello @');
      await tester.pump();

      expect(
        find.byKey(const ValueKey('composer-mention-pills')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('composer-mention-Ino')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('composer-mention-alice')),
        findsOneWidget,
      );

      // Escape dismisses at current @ offset.
      await tester.sendKeyEvent(LogicalKeyboardKey.escape);
      await tester.pump();
      expect(
        find.byKey(const ValueKey('composer-mention-pills')),
        findsNothing,
      );

      // Typing resets dismiss offset so mention pills reappear.
      await tester.enterText(field, 'Hello @a');
      await tester.pump();
      expect(
        find.byKey(const ValueKey('composer-mention-pills')),
        findsOneWidget,
      );

      // Clicking a pill inserts the handle.
      await tester.tap(find.byKey(const ValueKey('composer-mention-alice')));
      await tester.pump();
      final tf = tester.widget<TextField>(field);
      expect(tf.controller?.text, 'Hello @alice ');
    },
  );

  testWidgets(
    'Return to choice uses UiMixButton when chatting about a waiting choice',
    (tester) async {
      await tester.pumpWidget(
        hostConversation({
          'kind': 'conversation',
          'key': 'conv',
          'messages': <Map<String, dynamic>>[],
          'composer': {
            'key': 'composer',
            'onSubmit': {'target': '.', 'signal': 'ino.send'},
            'placeholder': 'Message...',
            'enabled': true,
          },
          'waiting': {
            'kind': 'choiceCard',
            'id': 'c1',
            'question': 'Pick one',
            'options': [
              {
                'title': 'Option A',
                'pick': {
                  'kind': 'button',
                  'key': 'opt-a',
                  'label': 'Option A',
                  'command': {'target': '.', 'signal': 'pick'},
                },
              },
              {
                'title': 'Option B',
                'pick': {
                  'kind': 'button',
                  'key': 'opt-b',
                  'label': 'Option B',
                  'command': {'target': '.', 'signal': 'pick'},
                },
              },
            ],
          },
          'status': 'ready',
        }),
      );
      await tester.pumpAndSettle();

      final chatAbout = find.text('Chat about this');
      expect(chatAbout, findsOneWidget);
      await tester.tap(chatAbout);
      await tester.pumpAndSettle();

      final returnBtn = find.byKey(const ValueKey('return-to-choice'));
      expect(returnBtn, findsOneWidget);
      expect(tester.widget(returnBtn), isA<UiMixButton>());

      await tester.tap(returnBtn);
      await tester.pumpAndSettle();
      expect(find.byKey(const ValueKey('return-to-choice')), findsNothing);
    },
  );
}
