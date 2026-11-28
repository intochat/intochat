import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  testWidgets(
    'renders self right-aligned, other human left-aligned with avatar + name, and @Ino Room only badge',
    (tester) async {
      final root = <String, dynamic>{
        'kind': 'conversation',
        'key': 'room-thread:alice:trip',
        'messages': [
          {
            'id': 'room-msg-1',
            'role': 'user',
            'text': 'Welcome everyone!',
            'status': 'completed',
            'sender': {
              'accountId': 'alice',
              'displayName': 'Alice',
              'kind': 'self',
            },
          },
          {
            'id': 'room-msg-2',
            'role': 'user',
            'text': 'Hey Alice, ready for the trip!',
            'status': 'completed',
            'sender': {
              'accountId': 'bob',
              'displayName': 'Bob Builder',
              'kind': 'human',
            },
          },
          {
            'id': 'room-msg-3',
            'role': 'assistant',
            'text': 'Here is a quick packing checklist for the room.',
            'status': 'completed',
            'sender': {
              'accountId': 'room-ino',
              'displayName': '@Ino · Room only',
              'kind': 'agent',
              'roomScoped': true,
            },
          },
          {
            'id': 'personal-msg-4',
            'role': 'assistant',
            'text': 'Personal 1:1 reply without room badge.',
            'status': 'completed',
            'sender': {
              'accountId': 'ino',
              'displayName': 'Ino',
              'kind': 'agent',
            },
          },
        ],
        'composer': {
          'key': 'input:room-message',
          'onSubmit': {'target': '.', 'signal': 'friends.send-room-message'},
          'placeholder': 'Message room',
          'enabled': true,
        },
        'status': 'ready',
      };

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light('intochat'),
          home: Scaffold(
            body: ViewHost(
              kind: 'view',
              name: 'local/apps/friends/view',
              overrides: ViewHostOverrides(
                knowsApp: (_) => false,
                load: (_, _) async => PublishedViewPayload.fromJson({
                  'generation': 1,
                  'view': {'title': 'Friends', 'root': root},
                }),
                watch: (_, _) => const Stream.empty(),
                onAction: (_) async => null,
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Self message is right-aligned without other-human avatar.
      expect(find.text('Welcome everyone!'), findsOneWidget);
      expect(find.byKey(const ValueKey('msg-human:room-msg-1')), findsNothing);

      // Other human message is left-aligned with initials avatar and display name.
      expect(
        find.byKey(const ValueKey('msg-human:room-msg-2')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('msg-avatar:room-msg-2')),
        findsOneWidget,
      );
      expect(find.text('BB'), findsOneWidget);
      expect(find.text('Bob Builder'), findsOneWidget);
      expect(find.text('Hey Alice, ready for the trip!'), findsOneWidget);

      final selfRect = tester.getRect(find.text('Welcome everyone!'));
      final otherRect = tester.getRect(
        find.text('Hey Alice, ready for the trip!'),
      );
      expect(selfRect.left, greaterThan(otherRect.left));

      // Room agent message shows distinct @Ino · Room only badge.
      expect(
        find.byKey(const ValueKey('msg-agent-badge:room-msg-3')),
        findsOneWidget,
      );
      expect(find.text('@Ino · Room only'), findsOneWidget);
      expect(
        find.text('Here is a quick packing checklist for the room.'),
        findsOneWidget,
      );

      // Personal 1:1 Ino reply (roomScoped: false/omitted) must NOT show the room badge.
      expect(
        find.byKey(const ValueKey('msg-agent-badge:personal-msg-4')),
        findsNothing,
      );
      expect(
        find.text('Personal 1:1 reply without room badge.'),
        findsOneWidget,
      );
    },
  );

  testWidgets(
    'nested Conversation inside Card -> Column (>4 children) under ViewHost renders without unbounded-height RenderFlex crash',
    (tester) async {
      final root = <String, dynamic>{
        'kind': 'card',
        'title': 'Rooms & Group Chat',
        'subtitle': 'Group · Trip Planning',
        'children': [
          {
            'kind': 'column',
            'gap': 'sm',
            'children': [
              {'kind': 'text', 'value': 'Room header line 1'},
              {'kind': 'text', 'value': 'Room header line 2'},
              {'kind': 'text', 'value': 'Room header line 3'},
              {'kind': 'text', 'value': 'Present: alice, bob'},
              {
                'kind': 'conversation',
                'key': 'room-thread:alice:trip',
                'messages': [
                  {
                    'id': 'nested-msg-1',
                    'role': 'user',
                    'text': 'Nested human message',
                    'status': 'completed',
                    'sender': {
                      'accountId': 'bob',
                      'displayName': 'Bob',
                      'kind': 'human',
                    },
                  },
                  {
                    'id': 'nested-msg-2',
                    'role': 'assistant',
                    'text': 'Nested @Ino reply',
                    'status': 'completed',
                    'sender': {
                      'accountId': 'room-ino',
                      'displayName': '@Ino · Room only',
                      'kind': 'agent',
                      'roomScoped': true,
                    },
                  },
                ],
                'composer': {
                  'key': 'input:room-message',
                  'onSubmit': {
                    'target': '.',
                    'signal': 'friends.send-room-message',
                  },
                  'placeholder': 'Message room',
                  'enabled': true,
                },
                'status': 'ready',
              },
            ],
          },
        ],
      };

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light('intochat'),
          home: Scaffold(
            body: ViewHost(
              kind: 'view',
              name: 'local/apps/friends/view',
              overrides: ViewHostOverrides(
                knowsApp: (_) => false,
                load: (_, _) async => PublishedViewPayload.fromJson({
                  'generation': 1,
                  'view': {'title': 'Friends', 'root': root},
                }),
                watch: (_, _) => const Stream.empty(),
                onAction: (_) async => null,
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
      expect(find.text('Nested human message'), findsOneWidget);
      expect(find.text('Nested @Ino reply'), findsOneWidget);
      expect(find.text('@Ino · Room only'), findsOneWidget);
      expect(find.text('Message room'), findsOneWidget);
    },
  );
}
