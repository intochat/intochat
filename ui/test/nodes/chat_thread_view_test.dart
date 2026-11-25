import 'dart:async';
import 'dart:math' as math;
import 'dart:typed_data';
import 'dart:ui' show Tristate;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:stream_channel/stream_channel.dart';
import 'package:web_socket_channel/web_socket_channel.dart';

Map<String, dynamic> _button(
  String key,
  String label,
  String signal, [
  Map<String, dynamic>? args,
]) => {
  'kind': 'button',
  'key': key,
  'label': label,
  'command': {'target': '.', 'signal': signal, 'args': ?args},
};

final _thread = {
  'kind': 'chatThreadView',
  'key': 'conversation',
  'messages': [
    {'id': 'u1', 'role': 'user', 'text': 'First question'},
    {
      'id': 'a1',
      'role': 'assistant',
      'text': '',
      'status': 'failed',
      'retry': _button('retry:a1', 'Try again', 'control.retry', {'id': 'a1'}),
      'error': {'kind': 'errorLine', 'what': 'The model did not answer.'},
    },
    {'id': 'u2', 'role': 'user', 'text': 'Second question'},
    {'id': 'a2', 'role': 'assistant', 'text': 'An answer.'},
  ],
  'composer': {
    'key': 'composer',
    'onSubmit': {'target': '.', 'signal': 'control.submitText'},
  },
  'stop': _button('stop', 'Stop', 'control.stop'),
  'retry': _button('error-action', 'Try again', 'control.retry', {'id': ''}),
};

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> props, {
  List<Map<String, dynamic>>? messages,
  VoiceTalker? talk,
  VoiceSpeaker? speak,
  AudioOut Function()? createAudioOut,
  MicStream Function()? createMicStream,
}) async {
  final sent = <Map<String, dynamic>>[];
  final thread = messages == null
      ? _thread
      : {..._thread, 'messages': messages};
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: NodeRenderer.render(
          Node.fromJson(thread),
          NodeContext(
            view: 'local/apps/ino/slots/conversation',
            generation: 3,
            actNeuron: 'local/apps/ino',
            slotPath: 'conversation',
            slotProps: props,
            talk: talk,
            speak: speak,
            createAudioOut: createAudioOut,
            createMicStream: createMicStream,
            send: (event) async {
              sent.add(Map<String, dynamic>.from(event));
              return {'delivered': true};
            },
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return sent;
}

class _TestSink implements WebSocketSink {
  _TestSink(this._inner);
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
      _sink = _TestSink(inner.sink);
  final Stream<dynamic> _stream;
  final _TestSink _sink;
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
  final _ctrl = StreamController<Uint8List>.broadcast();
  @override
  Future<Stream<Uint8List>> start() async => _ctrl.stream;
  @override
  Future<void> stop() async {
    if (!_ctrl.isClosed) await _ctrl.close();
  }
}

class _TestOut implements AudioOut {
  int clips = 0;
  int stops = 0;
  Completer<void>? _activeClip;

  @override
  Future<void> start(int sampleRate) async {}

  @override
  void push(Uint8List pcm16) {}

  @override
  void flush() {}

  @override
  Future<void> playClip(Uint8List wav) {
    clips++;
    final completer = Completer<void>();
    _activeClip = completer;
    return completer.future;
  }

  @override
  Future<void> stop() async {
    stops++;
    final active = _activeClip;
    _activeClip = null;
    if (active != null && !active.isCompleted) {
      active.complete();
    }
  }
}

void main() {
  testWidgets(
    'the send button is its own node; an empty composer disables no thread control',
    (tester) async {
      final semantics = tester.ensureSemantics();
      await _pump(tester, {'placeholder': 'Ask anything.', 'status': 'failed'});

      final send = tester.getSemantics(find.bySemanticsLabel('Send message'));
      expect(send.label, 'Send message');
      expect(send.flagsCollection.isButton, isTrue);
      expect(send.flagsCollection.isEnabled, Tristate.isFalse);

      // No node above the retry is a disabled button: the send button's state stays on its own node.
      var node = tester.getSemantics(find.text('Try again')).parent;
      while (node != null) {
        final flags = node.flagsCollection;
        final disabledButton =
            flags.isButton && flags.isEnabled == Tristate.isFalse;
        expect(disabledButton, isFalse, reason: 'ancestor "${node.label}"');
        node = node.parent;
      }
      semantics.dispose();
    },
  );

  testWidgets('a failed message draws its reason and a retry', (tester) async {
    await _pump(tester, {'placeholder': 'Ask anything.', 'status': 'failed'});

    expect(find.text('The model did not answer.'), findsOneWidget);
    expect(find.text('Try again'), findsOneWidget);
    expect(find.text('An answer.'), findsOneWidget);
  });

  testWidgets('the retry posts the thread\'s retry node for that message', (
    tester,
  ) async {
    final sent = await _pump(tester, {'placeholder': 'Ask anything.'});

    final node = Node.fromJson(_thread) as ChatThreadViewNode;
    final retry = node.messages[1].retry!;
    expect(retry.command!.signal, 'control.retry');
    expect(retry.command!.args, {'id': 'a1'});

    await tester.tap(find.text('Try again'));
    await tester.pumpAndSettle();

    expect(sent.single, {
      'kind': 'act',
      'neuron': 'local/apps/ino',
      'target': 'conversation',
      'signal': 'control.retry',
      'args': {'id': 'a1'},
    });
  });

  testWidgets('props draw the placeholder, the stop and an error to open', (
    tester,
  ) async {
    await _pump(tester, {
      'placeholder': 'Ask anything.',
      'working': true,
      'error': {
        'what': 'Ino has no model to answer with.',
        'actionLabel': 'Set up a model',
        'open': 'os://window/settings?section=models',
        'retry': false,
      },
    });

    expect(find.text('Ask anything.'), findsOneWidget);
    expect(find.text('Ino has no model to answer with.'), findsOneWidget);
    expect(find.text('Set up a model'), findsOneWidget);
    expect(find.text('Esc to stop'), findsOneWidget);
  });

  testWidgets('composer field draws no border of its own', (tester) async {
    await _pump(tester, {'placeholder': 'Ask anything.'});
    final field = tester.widget<TextField>(find.byType(TextField));
    expect(field.decoration!.border, InputBorder.none);
    expect(field.decoration!.enabledBorder, InputBorder.none);
    expect(field.decoration!.focusedBorder, InputBorder.none);
    expect(field.decoration!.disabledBorder, InputBorder.none);
    expect(field.decoration!.filled, isFalse);
  });

  testWidgets('one-line field, chip and send button share a centre line', (
    tester,
  ) async {
    await _pump(tester, {
      'placeholder': 'Ask anything.',
      'activeModelLabel': 'Qwen 2.5 Coder 14B',
    });
    final y = [
      find.byType(TextField),
      find.byKey(const ValueKey('composer-active-model')),
      find.byKey(const ValueKey('composer-send')),
    ].map((f) => tester.getCenter(f).dy);
    expect(y.reduce(math.max) - y.reduce(math.min), lessThanOrEqualTo(1.0));
  });

  testWidgets('ControlsStayAtBottomWhenTextGrows', (tester) async {
    await _pump(tester, {
      'placeholder': 'Ask anything.',
      'activeModelLabel': 'Qwen 2.5 Coder 14B',
    });
    await tester.enterText(
      find.byType(TextField),
      List.filled(6, 'line').join('\n'),
    );
    await tester.pump();

    expect(tester.getSize(find.byType(TextField)).height, greaterThan(100));
    expect(
      (tester.getBottomLeft(find.byKey(const ValueKey('composer-send'))).dy -
              tester.getBottomLeft(find.byType(TextField)).dy)
          .abs(),
      lessThanOrEqualTo(2.0),
    );
    expect(
      (tester
                  .getCenter(
                    find.byKey(const ValueKey('composer-active-model')),
                  )
                  .dy -
              tester.getCenter(find.byKey(const ValueKey('composer-send'))).dy)
          .abs(),
      lessThanOrEqualTo(1.0),
    );
  });

  testWidgets('a completed reply shows Regenerate with the refresh icon', (
    tester,
  ) async {
    final sent = await _pump(
      tester,
      {'placeholder': 'Ask anything.'},
      messages: [
        {'id': 'u1', 'role': 'user', 'text': 'Hello'},
        {
          'id': 'm2',
          'role': 'assistant',
          'text': 'hi',
          'status': 'completed',
          'retry': _button('regenerate:m2', 'Regenerate', 'control.retry', {
            'id': 'm2',
          }),
        },
      ],
    );

    final regen = find.byKey(const ValueKey('regenerate:m2'));
    expect(regen, findsOneWidget);
    expect(find.text('Regenerate'), findsOneWidget);
    expect(find.byIcon(LucideIcons.refreshCw), findsOneWidget);
    expect(find.byIcon(LucideIcons.rotateCcw), findsNothing);

    await tester.tap(regen);
    await tester.pumpAndSettle();

    expect(sent.single, {
      'kind': 'act',
      'neuron': 'local/apps/ino',
      'target': 'conversation',
      'signal': 'control.retry',
      'args': {'id': 'm2'},
    });
  });

  testWidgets('voice-mode button only with a talk conversation', (t) async {
    final talked = <String>[];
    Future<TalkSocket> fakeTalk(String conversation) async {
      talked.add(conversation);
      final pair = StreamChannelController<dynamic>(sync: true);
      return TalkSocket(_TestWsChannel(pair.local));
    }

    await _pump(
      t,
      {'placeholder': 'Ask anything.', 'talkConversation': null},
      talk: fakeTalk,
      createAudioOut: _TestOut.new,
      createMicStream: _TestMic.new,
    );
    expect(find.byKey(const ValueKey('composer-voice-mode')), findsNothing);

    await _pump(
      t,
      {'placeholder': 'Ask anything.', 'talkConversation': 'c-1'},
      talk: null,
      createAudioOut: _TestOut.new,
      createMicStream: _TestMic.new,
    );
    expect(find.byKey(const ValueKey('composer-voice-mode')), findsNothing);

    await _pump(
      t,
      {'placeholder': 'Ask anything.', 'talkConversation': 'c-1'},
      talk: fakeTalk,
      createAudioOut: _TestOut.new,
      createMicStream: _TestMic.new,
    );
    final voiceButton = find.byKey(const ValueKey('composer-voice-mode'));
    expect(voiceButton, findsOneWidget);

    // Voice-mode button shares vertical centre with field and send button.
    final y = [
      find.byType(TextField),
      voiceButton,
      find.byKey(const ValueKey('composer-send')),
    ].map((f) => t.getCenter(f).dy);
    expect(y.reduce(math.max) - y.reduce(math.min), lessThanOrEqualTo(1.0));

    // Tapping opens VoiceModeView for that conversation.
    // The voice orb animates continuously, so pump the route transition
    // rather than waiting for the tree to settle.
    await t.tap(voiceButton);
    await t.pump();
    await t.pump(const Duration(milliseconds: 350));
    expect(find.byType(VoiceModeView), findsOneWidget);
    expect(talked, ['c-1']);

    await t.tap(find.byKey(const ValueKey('voice-close')));
    await t.pump();
    await t.pump(const Duration(seconds: 1));
    expect(find.byType(VoiceModeView), findsNothing);
  });

  testWidgets('read aloud plays the reply and a second tap stops it', (
    t,
  ) async {
    final spoken = <String>[];
    final out = _TestOut();
    final wav = Uint8List.fromList([82, 73, 70, 70, 0, 0, 0, 0]);
    final messages = [
      {'id': 'u1', 'role': 'user', 'text': 'Hi'},
      {
        'id': 'm2',
        'role': 'assistant',
        'text': 'Hello there.',
        'status': 'completed',
      },
      {
        'id': 'm3',
        'role': 'assistant',
        'text': 'Still typing',
        'status': 'running',
      },
    ];

    await _pump(
      t,
      {'placeholder': 'Ask anything.', 'readAloud': false},
      messages: messages,
      speak: (text) async {
        spoken.add(text);
        return wav;
      },
      createAudioOut: () => out,
    );
    expect(find.byKey(const ValueKey('message-read-aloud:m2')), findsNothing);

    await _pump(
      t,
      {'placeholder': 'Ask anything.', 'readAloud': true},
      messages: messages,
      speak: (text) async {
        spoken.add(text);
        return wav;
      },
      createAudioOut: () => out,
    );
    expect(find.byKey(const ValueKey('message-read-aloud:m2')), findsOneWidget);
    // Running assistant reply does not show read-aloud.
    expect(find.byKey(const ValueKey('message-read-aloud:m3')), findsNothing);

    await t.tap(find.byKey(const ValueKey('message-read-aloud:m2')));
    await t.pump();
    expect(spoken, ['Hello there.']);
    expect(out.clips, 1);

    // Second tap stops playback instead of calling speak again.
    await t.tap(find.byKey(const ValueKey('message-read-aloud:m2')));
    await t.pump();
    expect(spoken, ['Hello there.']);
    expect(out.stops, greaterThanOrEqualTo(1));
  });
}
