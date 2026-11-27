import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:stream_channel/stream_channel.dart';
import 'package:web_socket_channel/web_socket_channel.dart';

class _FakeSink implements WebSocketSink {
  _FakeSink(this._inner);
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

class _FakeWsChannel extends StreamChannelMixin<dynamic>
    implements WebSocketChannel {
  _FakeWsChannel(StreamChannel<dynamic> inner)
    : _stream = inner.stream,
      _sink = _FakeSink(inner.sink);
  final Stream<dynamic> _stream;
  final _FakeSink _sink;
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

class _FakeMic implements MicStream {
  final _ctrl = StreamController<Uint8List>.broadcast();
  @override
  Future<Stream<Uint8List>> start() async => _ctrl.stream;
  @override
  Future<void> stop() async {
    if (!_ctrl.isClosed) await _ctrl.close();
  }
}

class _FakeAudioOut implements AudioOut {
  final List<Uint8List> playedClips = <Uint8List>[];
  int stopCalls = 0;
  int flushCalls = 0;
  Completer<void>? activeClip;
  bool throwOnPlay = false;

  @override
  Future<void> start(int sampleRate) async {}

  @override
  void push(Uint8List pcm16) {}

  @override
  void flush() {
    flushCalls++;
  }

  @override
  Future<void> playClip(Uint8List wav) {
    if (throwOnPlay) {
      return Future<void>.error(StateError('playback failed'));
    }
    playedClips.add(wav);
    final completer = Completer<void>();
    activeClip = completer;
    return completer.future;
  }

  @override
  Future<void> stop() async {
    stopCalls++;
    final active = activeClip;
    activeClip = null;
    if (active != null && !active.isCompleted) {
      active.complete();
    }
  }
}

MessageNode _message(
  String id, {
  String role = 'assistant',
  String text = '',
  String status = 'running',
}) => MessageNode.fromJson({
  'id': id,
  'role': role,
  'text': text,
  'status': status,
});

AttachmentNode _attachment() => AttachmentNode.fromJson({
  'id': 'att-1',
  'kind': 'voiceNote',
  'fileName': 'note.wav',
  'mediaType': 'audio/wav',
  'sizeBytes': 1024,
  'url': '/attachments/att-1',
  'durationMs': 1500,
  'transcript': 'hello',
});

Map<String, dynamic> _delta(
  String entry,
  String text,
  int at, {
  bool mapEntry = false,
}) => {
  'id': 'c-1',
  'entry': mapEntry ? {'value': entry} : entry,
  'delta': text,
  'at': at,
};

void main() {
  group('ConversationRpc', () {
    test('exposes canonical IConversation contract and methods', () {
      expect(
        ConversationRpc.contract,
        'IntoChat.OS.Modules.Flutter.Conversations.IConversation',
      );
      expect(ConversationRpc.textDeltaSignal, 'TextDelta');
      expect(ConversationRpc.readMethod, 'Read');
      expect(ConversationRpc.sayMethod, 'Say');
    });

    test('normalizeConversationKey trims and prefixes bare ids', () {
      expect(ConversationRpc.normalizeConversationKey(null), isNull);
      expect(ConversationRpc.normalizeConversationKey('   '), isNull);
      expect(
        ConversationRpc.normalizeConversationKey('c-1'),
        'conversations/c-1',
      );
      expect(
        ConversationRpc.normalizeConversationKey('  conversations/c-2  '),
        'conversations/c-2',
      );
    });

    test('attachmentToWireJson serializes optional fields when present', () {
      expect(ConversationRpc.attachmentToWireJson(_attachment()), {
        'id': 'att-1',
        'kind': 'voiceNote',
        'fileName': 'note.wav',
        'mediaType': 'audio/wav',
        'sizeBytes': 1024,
        'url': '/attachments/att-1',
        'durationMs': 1500,
        'transcript': 'hello',
      });
    });
  });

  group('ConversationController delta streaming & healing', () {
    test(
      'accumulates sequential and overlapping deltas without doubling',
      () async {
        final deltas = StreamController<Map<String, dynamic>>.broadcast();
        addTearDown(deltas.close);
        final running = _message('r1/reply');
        var notifications = 0;

        final controller = ConversationController(
          messages: [running],
          deltas: deltas.stream,
        )..addListener(() => notifications++);
        addTearDown(controller.dispose);

        deltas
          ..add(_delta('r1/reply', 'hel', 0))
          ..add(_delta('r1/reply', 'llo', 2, mapEntry: true))
          ..add(_delta('r1/reply', 'hel', 0))
          ..add(_delta('r1/reply', ' world', 5));
        await Future<void>.delayed(Duration.zero);

        expect(controller.pendingTextFor(running), 'hello world');
        expect(notifications, 3);
      },
    );

    test(
      'heals a gap via readConversation and replays queued deltas',
      () async {
        final deltas = StreamController<Map<String, dynamic>>.broadcast();
        addTearDown(deltas.close);
        final running = _message('r1/reply');
        final readCompleter = Completer<Map<String, dynamic>?>();
        var reads = 0;

        final controller = ConversationController(
          messages: [running],
          deltas: deltas.stream,
          readConversation: () {
            reads++;
            return readCompleter.future;
          },
        );
        addTearDown(controller.dispose);

        deltas
          ..add(_delta('r1/reply', 'world', 6))
          ..add(_delta('r1/reply', '!', 11));
        await Future<void>.delayed(Duration.zero);

        expect(reads, 1);
        expect(controller.isHealing, isTrue);

        readCompleter.complete({
          'active': {'runId': 'r1', 'reply': 'hello '},
        });
        await Future<void>.delayed(Duration.zero);

        expect(controller.isHealing, isFalse);
        expect(controller.pendingTextFor(running), 'hello world!');
      },
    );

    test(
      'clears pending text when message completes and ignores late deltas',
      () async {
        final deltas = StreamController<Map<String, dynamic>>.broadcast();
        addTearDown(deltas.close);
        final running = _message('r1/reply', status: 'running');
        final completed = _message(
          'r1/reply',
          text: 'final',
          status: 'completed',
        );

        final controller = ConversationController(
          messages: [running],
          deltas: deltas.stream,
        );
        addTearDown(controller.dispose);

        deltas.add(_delta('r1/reply', 'draft text', 0));
        await Future<void>.delayed(Duration.zero);
        expect(controller.pendingTextFor(running), 'draft text');

        controller.update(messages: [completed], deltas: deltas.stream);
        expect(controller.pending, isNull);
        expect(controller.pendingTextFor(completed), isNull);

        deltas.add(_delta('r1/reply', ' late', 10));
        await Future<void>.delayed(Duration.zero);
        expect(controller.pending, isNull);
      },
    );

    test('resets pending state when switching delta streams', () async {
      final first = StreamController<Map<String, dynamic>>.broadcast();
      final second = StreamController<Map<String, dynamic>>.broadcast();
      addTearDown(first.close);
      addTearDown(second.close);
      final running = _message('r1/reply');

      final controller = ConversationController(
        messages: [running],
        deltas: first.stream,
      );
      addTearDown(controller.dispose);

      first.add(_delta('r1/reply', 'first', 0));
      await Future<void>.delayed(Duration.zero);
      expect(controller.pendingTextFor(running), 'first');

      controller.update(messages: [running], deltas: second.stream);
      expect(controller.pending, isNull);

      first.add(_delta('r1/reply', ' stale', 5));
      second.add(_delta('r1/reply', 'second', 0));
      await Future<void>.delayed(Duration.zero);
      expect(controller.pendingTextFor(running), 'second');
    });
  });

  group('ConversationController audio & voice lifecycle', () {
    test(
      'read-aloud plays clip, stops on second tap, and barges in on switch',
      () async {
        final out = _FakeAudioOut();
        final spoken = <String>[];
        final wav = Uint8List.fromList([82, 73, 70, 70, 0, 0, 0, 0]);

        final controller = ConversationController(
          speak: (text) async {
            spoken.add(text);
            return wav;
          },
          createAudioOut: () => out,
        );
        addTearDown(controller.dispose);

        final firstPlay = controller.toggleReadAloud('m1', 'Hello');
        await Future<void>.delayed(Duration.zero);
        expect(controller.readingMessageId, 'm1');
        expect(spoken, ['Hello']);
        expect(out.playedClips.length, 1);

        // Switching to another message stops the first clip before playing the second.
        final secondPlay = controller.toggleReadAloud('m2', 'World');
        await Future<void>.delayed(Duration.zero);
        await firstPlay;
        expect(controller.readingMessageId, 'm2');
        expect(out.stopCalls, greaterThanOrEqualTo(1));
        expect(out.playedClips.length, 2);

        // Second tap on active message stops playback.
        await controller.toggleReadAloud('m2', 'World');
        await secondPlay;
        expect(controller.readingMessageId, isNull);
      },
    );

    test(
      'read-aloud recovers state cleanly on synthesis/playback error',
      () async {
        final out = _FakeAudioOut()..throwOnPlay = true;
        final controller = ConversationController(
          speak: (_) async => Uint8List.fromList([1, 2, 3, 4]),
          createAudioOut: () => out,
        );
        addTearDown(controller.dispose);

        await controller.toggleReadAloud('m1', 'Boom');
        expect(controller.readingMessageId, isNull);
      },
    );

    test(
      'opening voice mode stops active read-aloud and binds AudioOut',
      () async {
        final readOut = _FakeAudioOut();
        final voiceOut = _FakeAudioOut();
        var created = 0;

        final controller = ConversationController(
          speak: (_) async => Uint8List.fromList([1, 2, 3, 4]),
          talk: (_) async {
            final pair = StreamChannelController<dynamic>(sync: true);
            return TalkSocket(_FakeWsChannel(pair.local));
          },
          talkConversation: 'c-1',
          createMicStream: _FakeMic.new,
          createAudioOut: () => created++ == 0 ? readOut : voiceOut,
        );
        addTearDown(controller.dispose);

        final play = controller.toggleReadAloud('m1', 'Speaking');
        await Future<void>.delayed(Duration.zero);
        expect(controller.readingMessageId, 'm1');

        controller.toggleVoiceMode('c-1');
        await play;

        expect(controller.readingMessageId, isNull);
        expect(readOut.stopCalls, greaterThanOrEqualTo(1));
        expect(controller.voiceSession, isNotNull);
        expect(controller.voiceSession!.conversation, 'c-1');
        expect(identical(controller.voiceSession!.out, voiceOut), isTrue);

        // Clearing talkConversation closes the voice session.
        controller.update(
          messages: const [],
          talk: null,
          talkConversation: null,
        );
        expect(controller.voiceSession, isNull);
      },
    );

    test('dispose stops active read-aloud AudioOut', () async {
      final out = _FakeAudioOut();
      final controller = ConversationController(
        speak: (_) async => Uint8List.fromList([1, 2, 3, 4]),
        createAudioOut: () => out,
      );

      final play = controller.toggleReadAloud('m1', 'Playing');
      await Future<void>.delayed(Duration.zero);
      expect(controller.readingMessageId, 'm1');

      controller.dispose();
      await play;
      expect(out.stopCalls, greaterThanOrEqualTo(1));
    });
  });

  group('ConversationController.sendWithAttachments', () {
    test('uses explicitSend when provided', () async {
      final controller = ConversationController();
      addTearDown(controller.dispose);
      String? sentDraft;
      List<AttachmentNode>? sentAttachments;

      final ok = await controller.sendWithAttachments(
        draft: 'check this',
        attachments: [_attachment()],
        explicitSend: (draft, attachments) async {
          sentDraft = draft;
          sentAttachments = attachments;
        },
        callNeuron: null,
        conversationKey: null,
      );

      expect(ok, isTrue);
      expect(sentDraft, 'check this');
      expect(sentAttachments, hasLength(1));
    });

    test(
      'falls back to callNeuron with ConversationRpc.contract and Say',
      () async {
        final controller = ConversationController();
        addTearDown(controller.dispose);
        final calls = <(String, String, String, List<Object?>)>[];

        final ok = await controller.sendWithAttachments(
          draft: 'voice note',
          attachments: [_attachment()],
          explicitSend: null,
          callNeuron: (contract, key, method, args) async {
            calls.add((contract, key, method, args));
            return null;
          },
          conversationKey: 'c-42',
        );

        expect(ok, isTrue);
        expect(calls, hasLength(1));
        final (contract, key, method, args) = calls.single;
        expect(contract, ConversationRpc.contract);
        expect(key, 'conversations/c-42');
        expect(method, ConversationRpc.sayMethod);
        expect(args[0], 'voice note');
        expect(args[1], isNull);
        expect(args[2], [ConversationRpc.attachmentToWireJson(_attachment())]);
      },
    );

    test('returns false when neither transport is available', () async {
      final controller = ConversationController();
      addTearDown(controller.dispose);

      final ok = await controller.sendWithAttachments(
        draft: 'nope',
        attachments: [_attachment()],
        explicitSend: null,
        callNeuron: null,
        conversationKey: null,
      );

      expect(ok, isFalse);
    });
  });
}
