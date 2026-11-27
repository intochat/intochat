import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:stream_channel/stream_channel.dart';
import 'package:web_socket_channel/web_socket_channel.dart';

class _TestWsSink implements WebSocketSink {
  _TestWsSink(this._inner);
  final StreamSink<dynamic> _inner;

  @override
  void add(dynamic data) => _inner.add(data);

  @override
  void addError(Object error, [StackTrace? stackTrace]) =>
      _inner.addError(error, stackTrace);

  @override
  Future addStream(Stream stream) => _inner.addStream(stream);

  @override
  Future close([int? closeCode, String? closeReason]) => _inner.close();

  @override
  Future get done => _inner.done;
}

class _TestWsChannel extends StreamChannelMixin<dynamic>
    implements WebSocketChannel {
  _TestWsChannel(StreamChannel<dynamic> inner)
    : _stream = inner.stream,
      _sink = _TestWsSink(inner.sink);

  final Stream<dynamic> _stream;
  final _TestWsSink _sink;

  @override
  Stream<dynamic> get stream => _stream;

  @override
  WebSocketSink get sink => _sink;

  @override
  Future<void> get ready => Future<void>.value();

  @override
  int? get closeCode => null;

  @override
  String? get closeReason => null;

  @override
  String? get protocol => null;
}

class _TestMic implements MicStream {
  final _controller = StreamController<Uint8List>.broadcast(sync: true);
  bool stopped = false;

  @override
  Future<Stream<Uint8List>> start() async => _controller.stream;

  @override
  Future<void> stop() async {
    stopped = true;
    if (!_controller.isClosed) await _controller.close();
  }
}

class _TestOut implements AudioOut {
  int stops = 0;

  @override
  Future<void> start(int sampleRate) async {}

  @override
  void push(Uint8List pcm16) {}

  @override
  void flush() {}

  @override
  Future<void> playClip(Uint8List wav) async {}

  @override
  Future<void> stop() async {
    stops++;
  }
}

void main() {
  testWidgets(
    'Co-Presence: thread messages and ApprovalCardNode stay visible and tappable while Voice HUD is active',
    (tester) async {
      tester.view.physicalSize = const Size(1024, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final pair = StreamChannelController<dynamic>(sync: true);
      final sentJson = <Map<String, Object?>>[];
      pair.foreign.stream.listen((frame) {
        if (frame is String) {
          sentJson.add(Map<String, Object?>.from(jsonDecode(frame) as Map));
        }
      });

      final mic = _TestMic();
      final out = _TestOut();
      var talkConnects = 0;
      final sentActions = <Map<String, dynamic>>[];

      final node = Node.fromJson({
        'kind': 'conversation',
        'key': 'conv',
        'messages': [
          {
            'id': 'm1',
            'role': 'user',
            'text': 'Update the changelog for release 1.4',
            'status': 'completed',
          },
          {
            'id': 'm2',
            'role': 'assistant',
            'text': 'I need your approval before writing the file.',
            'status': 'completed',
            'cards': [
              {
                'kind': 'approvalCard',
                'key': 'approval-1',
                'id': 'plan-42',
                'title': 'Write release notes to docs/CHANGELOG.md',
                'summary': 'Creates a new section for v1.4',
                'systemName': 'Files',
                'status': 'pending',
                'rows': [
                  {'label': 'Path', 'newValue': 'docs/CHANGELOG.md'},
                ],
                'resolve': {'planId': 'plan-42'},
              },
            ],
          },
        ],
        'composer': {
          'key': 'composer',
          'placeholder': 'Ask anything.',
          'onSubmit': {'target': '.', 'signal': 'control.submitText'},
        },
      }) as ConversationNode;

      final ctx = NodeContext(
        view: 'local/apps/ino/children/conversation',
        generation: 1,
        actNeuron: 'local/apps/ino',
        childPath: 'conversation',
        confirmation: true,
        send: (event) async {
          sentActions.add(Map<String, dynamic>.from(event));
          return {'delivered': true};
        },
        talk: (conversation) async {
          talkConnects++;
          return TalkSocket(_TestWsChannel(pair.local));
        },
        createMicStream: () => mic,
        createAudioOut: () => out,
      );

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.intochat(Brightness.light),
          home: Scaffold(
            body: ConversationView(
              node: node,
              ctx: ctx,
              talkConversation: 'c-42',
            ),
          ),
        ),
      );

      // Open Voice HUD inline (not a fullscreen modal route).
      await tester.tap(find.byKey(const ValueKey('composer-voice-mode')));
      await tester.pump();

      expect(find.byType(VoiceModeView), findsOneWidget);
      expect(find.byKey(const ValueKey('voice-hud-compact')), findsOneWidget);
      expect(talkConnects, 1);

      // Thread messages AND ApprovalCardNode remain visible simultaneously.
      expect(
        find.textContaining(
          'Update the changelog for release 1.4',
          findRichText: true,
        ),
        findsOneWidget,
      );
      expect(
        find.textContaining(
          'I need your approval before writing the file.',
          findRichText: true,
        ),
        findsOneWidget,
      );
      expect(find.byKey(const ValueKey('approval:plan-42')), findsOneWidget);

      // Emit a live tool chip on /ai/talk while approval is waiting.
      pair.foreign.sink.add(
        jsonEncode({
          'type': 'tool',
          'callId': 'tc-write',
          'name': 'files_write',
          'status': 'running',
          'label': 'Waiting for file approval',
        }),
      );
      await tester.pump();
      expect(find.text('Waiting for file approval'), findsOneWidget);

      // Tap Approve on the ApprovalCardNode without closing Voice Mode.
      final approveButton = find.descendant(
        of: find.byKey(const ValueKey('approval:plan-42')),
        matching: find.text('Approve'),
      );
      expect(approveButton, findsOneWidget);
      await tester.tap(approveButton);
      await tester.pump();

      expect(sentActions, hasLength(1));
      expect(sentActions.single['planId'], 'plan-42');
      expect(sentActions.single['approve'], isTrue);

      // Voice HUD is still active and connected (no `end` frame sent).
      expect(find.byType(VoiceModeView), findsOneWidget);
      expect(sentJson.where((m) => m['type'] == 'end'), isEmpty);

      // Closing Voice HUD tears down cleanly.
      await tester.tap(find.byKey(const ValueKey('voice-close')));
      await tester.pump();
      expect(find.byType(VoiceModeView), findsNothing);
      expect(sentJson.last, {'type': 'end'});
      expect(mic.stopped, isTrue);
      expect(out.stops, greaterThanOrEqualTo(1));
    },
  );
}
