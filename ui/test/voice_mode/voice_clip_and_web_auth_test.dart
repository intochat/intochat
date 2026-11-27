import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/src/chat/voice_clip_card.dart';

class _StubAudioOut implements AudioOut {
  int playClipCalls = 0;
  int stopCalls = 0;
  Uint8List? lastClip;

  @override
  Future<void> start(int sampleRate) async {}

  @override
  void push(Uint8List pcm16) {}

  @override
  Future<void> flush() async {}

  @override
  Future<void> playClip(Uint8List bytes) async {
    playClipCalls++;
    lastClip = bytes;
  }

  @override
  Future<void> stop() async {
    stopCalls++;
  }
}

void main() {
  testWidgets(
    'VoiceClipCard renders duration, toggles transcript, and plays fetched audio',
    (tester) async {
      final audioOut = _StubAudioOut();
      final fetchedUrls = <String>[];
      final fakeWav = Uint8List.fromList([0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4]);

      const attachment = AttachmentNode(
        id: '0123456789abcdef0123456789abcdef',
        kind: 'voice-note',
        fileName: 'note.wav',
        mediaType: 'audio/wav',
        sizeBytes: 32000,
        url: '/ai/attachments/0123456789abcdef0123456789abcdef',
        durationMs: 12000,
        transcript: 'Redacted voice note summary.',
      );

      final ctx = NodeContext(
        view: 'local/apps/ino/view',
        fetchAttachment: (url) async {
          fetchedUrls.add(url);
          return fakeWav;
        },
        createAudioOut: () => audioOut,
      );

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light('intochat'),
          home: Scaffold(
            body: Center(
              child: VoiceClipCard(attachment: attachment, ctx: ctx),
            ),
          ),
        ),
      );

      expect(find.text('0:12'), findsOneWidget);
      expect(find.text('Transcript'), findsOneWidget);
      expect(find.text('Redacted voice note summary.'), findsNothing);

      // Expand transcript.
      await tester.tap(
        find.byKey(
          const ValueKey(
            'voice-clip-transcript-toggle:0123456789abcdef0123456789abcdef',
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Redacted voice note summary.'), findsOneWidget);
      expect(find.text('Hide transcript'), findsOneWidget);

      // Play voice clip.
      await tester.tap(
        find.byKey(
          const ValueKey('voice-clip-play:0123456789abcdef0123456789abcdef'),
        ),
      );
      await tester.pumpAndSettle();
      expect(fetchedUrls, ['/ai/attachments/0123456789abcdef0123456789abcdef']);
      expect(audioOut.playClipCalls, 1);
      expect(audioOut.lastClip, fakeWav);
    },
  );

  testWidgets(
    'ConversationView renders VoiceClipCard, AttachmentPreviewCard, and keeps dictation alongside voice-note composer',
    (tester) async {
      const voiceNote = AttachmentNode(
        id: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        kind: 'voice-note',
        fileName: 'clip.wav',
        mediaType: 'audio/wav',
        sizeBytes: 16000,
        url: '/ai/attachments/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        durationMs: 5000,
        transcript: 'Hello Ino',
      );
      const fileAtt = AttachmentNode(
        id: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
        kind: 'file',
        fileName: 'architecture.pdf',
        mediaType: 'application/pdf',
        sizeBytes: 2048,
        url: '/ai/attachments/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
        durationMs: null,
        transcript: null,
      );

      final node = ConversationNode.fromJson({
        'kind': 'conversation',
        'key': 'thread',
        'messages': [
          {
            'id': 'm1',
            'role': 'user',
            'text': 'See attached',
            'status': 'completed',
            'attachments': [
              {
                'id': voiceNote.id,
                'kind': voiceNote.kind,
                'fileName': voiceNote.fileName,
                'mediaType': voiceNote.mediaType,
                'sizeBytes': voiceNote.sizeBytes,
                'url': voiceNote.url,
                'durationMs': voiceNote.durationMs,
                'transcript': voiceNote.transcript,
              },
              {
                'id': fileAtt.id,
                'kind': fileAtt.kind,
                'fileName': fileAtt.fileName,
                'mediaType': fileAtt.mediaType,
                'sizeBytes': fileAtt.sizeBytes,
                'url': fileAtt.url,
              },
            ],
          },
        ],
        'composer': {
          'key': 'composer',
          'onSubmit': {'target': '.', 'signal': 'control.submitText'},
          'placeholder': 'Message',
          'enabled': true,
          'dictation': true,
        },
        'status': 'ready',
        'automationName': 'thread',
      });

      final ctx = NodeContext(
        view: 'local/apps/ino/view',
        send: (_) async => null,
        transcribe: (_, _) async => 'dictated draft',
        uploadAttachment: (
          bytes,
          fileName, {
          required mediaType,
          isVoiceNote = false,
          durationMs,
        }) async => voiceNote,
      );

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light('intochat'),
          home: Scaffold(
            body: ConversationView(node: node, ctx: ctx),
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Both VoiceClipCard and AttachmentPreviewCard render inside the message bubble.
      expect(
        find.byKey(
          const ValueKey('voice-clip-card:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'),
        ),
        findsOneWidget,
      );
      expect(
        find.byKey(
          const ValueKey('attachment-card:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'),
        ),
        findsOneWidget,
      );
      expect(find.text('architecture.pdf'), findsOneWidget);

      // Both mic-to-draft dictation AND voice-note clip buttons are present with distinct tooltips.
      expect(find.byTooltip('Record a voice draft'), findsOneWidget);
      expect(find.byTooltip('Record a voice-note clip'), findsOneWidget);
    },
  );

  test('IntoChatOSUiClient.mintTalkTicket mints opaque ticket via POST /ai/talk/ticket with zero query bearer tokens', () async {
    http.Request? captured;
    final mockHttp = MockClient((request) async {
      captured = request;
      return http.Response(
        jsonEncode({'ticket': 'opaque-ticket-abc123', 'expiresInSeconds': 30}),
        200,
        headers: {'content-type': 'application/json'},
      );
    });

    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('https://os.example.com'),
      httpClient: mockHttp,
      credentials: const BasicCredentials(
        username: 'alice',
        password: 'secret',
      ),
    );

    final ticket = await client.mintTalkTicket('main');
    expect(ticket, 'opaque-ticket-abc123');
    expect(captured, isNotNull);
    expect(captured!.method, 'POST');
    expect(captured!.url.path, '/ai/talk/ticket');
    expect(captured!.url.query, isEmpty);
    expect(jsonDecode(captured!.body), {'conversation': 'main'});
    expect(captured!.headers['authorization'], isNotEmpty);
  });
}
