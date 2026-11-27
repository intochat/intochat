import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:stream_channel/stream_channel.dart';
import 'package:web_socket_channel/web_socket_channel.dart';

class _FakeWebSocketSink implements WebSocketSink {
  _FakeWebSocketSink(this._inner);

  final StreamSink<dynamic> _inner;
  int? closeCode;
  String? closeReason;

  @override
  void add(dynamic data) => _inner.add(data);

  @override
  void addError(Object error, [StackTrace? stackTrace]) =>
      _inner.addError(error, stackTrace);

  @override
  Future addStream(Stream stream) => _inner.addStream(stream);

  @override
  Future close([int? closeCode, String? closeReason]) {
    this.closeCode = closeCode;
    this.closeReason = closeReason;
    return _inner.close();
  }

  @override
  Future get done => _inner.done;
}

class _FakeWebSocketChannel extends StreamChannelMixin<dynamic>
    implements WebSocketChannel {
  _FakeWebSocketChannel(StreamChannel<dynamic> inner)
    : _stream = inner.stream,
      _sink = _FakeWebSocketSink(inner.sink);

  final Stream<dynamic> _stream;
  final _FakeWebSocketSink _sink;

  @override
  Stream<dynamic> get stream => _stream;

  @override
  WebSocketSink get sink => _sink;

  @override
  Future<void> get ready => Future<void>.value();

  @override
  int? get closeCode => _sink.closeCode;

  @override
  String? get closeReason => _sink.closeReason;

  @override
  String? get protocol => null;
}

void main() {
  test('TalkEvent parses every kind', () {
    final types = ['state', 'heard', 'reply', 'tool', 'cancel', 'error']
        .map(
          (k) => TalkEvent.fromJson({
            'type': k,
            'state': 'listening',
            'text': 'x',
            'message': 'm',
            'callId': 'tc-1',
            'name': 'web_search',
            'status': 'running',
            'label': 'Searching the web',
          }).runtimeType,
        )
        .toSet();
    expect(types.length, 6);

    expect(
      (TalkEvent.fromJson({
        'type': 'state',
        'state': 'thinking',
      }) as TalkStateEvent).state,
      TalkPhase.thinking,
    );
    expect(
      (TalkEvent.fromJson({
        'type': 'state',
        'state': 'speaking',
      }) as TalkStateEvent).state,
      TalkPhase.speaking,
    );
    expect(
      (TalkEvent.fromJson({
        'type': 'heard',
        'text': 'hello',
      }) as TalkHeardEvent).text,
      'hello',
    );
    expect(
      (TalkEvent.fromJson({
        'type': 'reply',
        'text': 'hi there',
      }) as TalkReplyEvent).text,
      'hi there',
    );
    expect(TalkEvent.fromJson({'type': 'cancel'}), isA<TalkCancelEvent>());
    expect(
      (TalkEvent.fromJson({
        'type': 'error',
        'message': 'unavailable',
      }) as TalkErrorEvent).message,
      'unavailable',
    );
  });

  test('TalkToolEvent parses running/done/failed, optional label, missing fields, and unknown extras', () {
    final running = TalkEvent.fromJson({
      'type': 'tool',
      'callId': 'call-1',
      'name': 'web_search',
      'status': 'running',
      'label': 'Searching docs',
      'futureField': {'nested': 42},
    }) as TalkToolEvent;
    expect(running.callId, 'call-1');
    expect(running.name, 'web_search');
    expect(running.status, TalkToolStatus.running);
    expect(running.label, 'Searching docs');
    expect(running.displayLabel, 'Searching docs');

    final done = TalkEvent.fromJson({
      'type': 'tool',
      'callId': 'call-1',
      'name': 'data_query',
      'status': 'done',
    }) as TalkToolEvent;
    expect(done.status, TalkToolStatus.done);
    expect(done.label, isNull);
    expect(done.displayLabel, 'data_query');

    final failed = TalkEvent.fromJson({
      'type': 'tool',
      'callId': 'call-2',
      'name': 'files_write',
      'status': 'failed',
      'label': '   ',
    }) as TalkToolEvent;
    expect(failed.status, TalkToolStatus.failed);
    expect(failed.label, isNull);
    expect(failed.displayLabel, 'files_write');

    // Missing optional fields and unknown future status do not crash.
    final minimal = TalkEvent.fromJson({
      'type': 'tool',
      'status': 'streaming_partial',
    }) as TalkToolEvent;
    expect(minimal.callId, '');
    expect(minimal.name, '');
    expect(minimal.status, TalkToolStatus.running);
    expect(minimal.label, isNull);
  });

  test('TalkSocket ignores unknown future event types without interrupting valid frames', () async {
    final pair = StreamChannelController<dynamic>(sync: true);
    final socket = TalkSocket(_FakeWebSocketChannel(pair.local));
    final events = <TalkEvent>[];
    socket.events.listen(events.add);

    pair.foreign.sink.add(
      jsonEncode({'type': 'future_telemetry', 'latencyMs': 12}),
    );
    pair.foreign.sink.add(
      jsonEncode({
        'type': 'tool',
        'callId': 'c-7',
        'name': 'focus_show',
        'status': 'done',
        'label': 'Showing chart',
      }),
    );

    await socket.close();
    expect(events, hasLength(1));
    final tool = events.single as TalkToolEvent;
    expect(tool.callId, 'c-7');
    expect(tool.name, 'focus_show');
    expect(tool.status, TalkToolStatus.done);
    expect(tool.displayLabel, 'Showing chart');
  });

  test(
    'TalkSocket splits text events and binary audio and sends control frames',
    () async {
      final pair = StreamChannelController<dynamic>(sync: true);
      final sent = <dynamic>[];
      pair.foreign.stream.listen(sent.add);

      final socket = TalkSocket(_FakeWebSocketChannel(pair.local));
      final events = <TalkEvent>[];
      final audio = <Uint8List>[];
      socket.events.listen(events.add);
      socket.audio.listen(audio.add);

      pair.foreign.sink.add(jsonEncode({'type': 'state', 'state': 'speaking'}));
      pair.foreign.sink.add(Uint8List.fromList([1, 2, 3, 4]));
      pair.foreign.sink.add(jsonEncode({'type': 'reply', 'text': 'Hello'}));
      pair.foreign.sink.add(jsonEncode({'type': 'cancel'}));

      socket.send(Uint8List.fromList([9, 8, 7, 6]));
      socket.interrupt();
      await socket.close();

      expect(events, hasLength(3));
      expect(events[0], isA<TalkStateEvent>());
      expect(events[1], isA<TalkReplyEvent>());
      expect(events[2], isA<TalkCancelEvent>());
      expect(audio.single, Uint8List.fromList([1, 2, 3, 4]));

      expect(sent, hasLength(3));
      expect(sent[0], Uint8List.fromList([9, 8, 7, 6]));
      expect(jsonDecode(sent[1] as String), {'type': 'interrupt'});
      expect(jsonDecode(sent[2] as String), {'type': 'end'});
    },
  );

  test(
    'IntoChatOSUiClient.speak posts text to /ai/speech and returns audio bytes',
    () async {
      final wav = Uint8List.fromList([82, 73, 70, 70, 1, 2, 3, 4]);
      Uri? requestedUri;
      String? requestedMethod;
      Map<String, dynamic>? requestedBody;

      final client = IntoChatOSUiClient(
        baseUri: Uri.parse('http://localhost:5000'),
        httpClient: MockClient((request) async {
          requestedUri = request.url;
          requestedMethod = request.method;
          requestedBody = jsonDecode(request.body) as Map<String, dynamic>;
          return http.Response.bytes(
            wav,
            200,
            headers: {'content-type': 'audio/wav'},
          );
        }),
      );

      final bytes = await client.speak('Hello from IntoChat.');
      expect(requestedMethod, 'POST');
      expect(requestedUri?.path, '/ai/speech');
      expect(requestedBody, {'text': 'Hello from IntoChat.'});
      expect(bytes, wav);
    },
  );

  test('IntoChatOSUiClient.talkUri maps http/https to ws/wss with conversation query', () {
    expect(
      IntoChatOSUiClient.talkUri(Uri.parse('http://localhost:5050'), 'c-1'),
      Uri.parse('ws://localhost:5050/ai/talk?conversation=c-1'),
    );
    expect(
      IntoChatOSUiClient.talkUri(Uri.parse('https://example.com'), 'main'),
      Uri.parse('wss://example.com/ai/talk?conversation=main'),
    );
  });
}
