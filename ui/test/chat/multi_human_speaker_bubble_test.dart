import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  testWidgets(
    'SpeakerBubble renders self, human (avatar + name + bordered card), and agent with room-only tag',
    (tester) async {
      const c = UiColors.intochatLight;

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.intochat(Brightness.light),
          home: const Scaffold(
            body: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                SpeakerBubble(
                  kind: SpeakerKind.self,
                  child: Text('My own message'),
                ),
                SizedBox(height: 12),
                SpeakerBubble(
                  kind: SpeakerKind.human,
                  senderName: 'Ada Lovelace',
                  child: Text('Hello from another human'),
                ),
                SizedBox(height: 12),
                SpeakerBubble(
                  kind: SpeakerKind.agent,
                  roomOnlyTag: '@Ino · Room only',
                  child: Text('Agent summary for this room'),
                ),
              ],
            ),
          ),
        ),
      );

      // Self bubble uses bubbleMine background.
      expect(find.text('My own message'), findsOneWidget);

      // Human bubble renders 24px initials avatar, 12px w600 textMuted name,
      // and surfaceCard + 1px borderHairline container.
      expect(find.text('AL'), findsOneWidget);
      expect(
        tester.getSize(find.byKey(const ValueKey('speaker_human_avatar'))),
        const Size(24, 24),
      );
      final senderText = tester.widget<Text>(
        find.byKey(const ValueKey('speaker_human_name')),
      );
      expect(senderText.data, 'Ada Lovelace');
      expect(senderText.style?.fontSize, 12);
      expect(senderText.style?.fontWeight, FontWeight.w600);
      expect(senderText.style?.color, c.textMuted);

      final humanBubble = tester.widget<Container>(
        find.byKey(const ValueKey('speaker_human_bubble')),
      );
      final dec = humanBubble.decoration! as BoxDecoration;
      expect(dec.color, c.surfaceCard);
      expect((dec.border! as Border).top.color, c.borderHairline);

      // Agent bubble renders optional @Ino · Room only TagPill.
      expect(
        find.byKey(const ValueKey('speaker_agent_room_tag')),
        findsOneWidget,
      );
      expect(find.text('@Ino · Room only'), findsOneWidget);
      expect(find.text('Agent summary for this room'), findsOneWidget);
    },
  );
}
