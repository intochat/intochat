import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
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

class _FakeServer {
  _FakeServer() {
    _pair.foreign.stream.listen((frame) {
      if (frame is Uint8List) {
        sentBinary.add(frame);
      } else if (frame is List<int>) {
        sentBinary.add(Uint8List.fromList(frame));
      } else if (frame is String) {
        sentJson.add(Map<String, Object?>.from(jsonDecode(frame) as Map));
      }
    });
  }

  final _pair = StreamChannelController<dynamic>(sync: true);
  final List<Uint8List> sentBinary = [];
  final List<Map<String, Object?>> sentJson = [];

  TalkSocket createSocket() => TalkSocket(_FakeWebSocketChannel(_pair.local));

  void event(Map<String, Object?> payload) {
    _pair.foreign.sink.add(jsonEncode(payload));
  }

  void audio(Uint8List pcm16) {
    _pair.foreign.sink.add(pcm16);
  }
}

class _FakeMic implements MicStream {
  final _controller = StreamController<Uint8List>.broadcast(sync: true);
  bool started = false;
  bool stopped = false;

  @override
  Future<Stream<Uint8List>> start() async {
    started = true;
    return _controller.stream;
  }

  void emit(Uint8List pcm16) {
    _controller.add(pcm16);
  }

  @override
  Future<void> stop() async {
    stopped = true;
    if (!_controller.isClosed) {
      await _controller.close();
    }
  }
}

class _FakeOut implements AudioOut {
  int? startedSampleRate;
  final List<Uint8List> pushed = [];
  int flushes = 0;
  int clips = 0;
  int stops = 0;

  @override
  Future<void> start(int sampleRate) async {
    startedSampleRate = sampleRate;
  }

  @override
  void push(Uint8List pcm16) {
    pushed.add(pcm16);
  }

  @override
  void flush() {
    flushes++;
  }

  @override
  Future<void> playClip(Uint8List wav) async {
    clips++;
  }

  @override
  Future<void> stop() async {
    stops++;
  }
}

Widget _host(Widget child) =>
    MaterialApp(theme: UiTheme.intochat(Brightness.light), home: child);

Future<({_FakeServer server, _FakeMic mic, _FakeOut out})> _pumpOrbRoute(
  WidgetTester tester,
) async {
  final server = _FakeServer();
  final socket = server.createSocket();
  final mic = _FakeMic();
  final out = _FakeOut();

  await tester.pumpWidget(
    _host(
      Scaffold(
        body: Builder(
          builder: (context) => TextButton(
            onPressed: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  fullscreenDialog: true,
                  builder: (_) => VoiceModeView(
                    connect: () async => socket,
                    mic: mic,
                    out: out,
                  ),
                ),
              );
            },
            child: const Text('Open'),
          ),
        ),
      ),
    ),
  );
  await tester.tap(find.text('Open'));
  await tester.pump();
  await tester.pump(const Duration(milliseconds: 350));
  return (server: server, mic: mic, out: out);
}

void main() {
  testWidgets(
    'mic frames reach the socket and reply audio reaches the speaker',
    (t) async {
      final server = _FakeServer();
      final socket = server.createSocket();
      final mic = _FakeMic();
      final out = _FakeOut();

      await t.pumpWidget(
        _host(VoiceModeView(connect: () async => socket, mic: mic, out: out)),
      );
      mic.emit(Uint8List(640));
      server.audio(Uint8List(960));
      await t.pump();

      expect(out.startedSampleRate, 24000);
      expect(server.sentBinary.single.length, 640);
      expect(out.pushed.single.length, 960);
    },
  );

  testWidgets('cancel from the Kernel drops queued audio', (t) async {
    final h = await _pumpOrbRoute(t);
    h.server.event({'type': 'cancel'});
    await t.pump();
    expect(h.out.flushes, 1);
  });

  testWidgets('tapping the orb interrupts', (t) async {
    final h = await _pumpOrbRoute(t);
    await t.tap(find.byKey(const ValueKey('voice-orb')));
    await t.pump();
    expect(h.server.sentJson.last, {'type': 'interrupt'});
    expect(h.out.flushes, 1);
  });

  testWidgets('close ends the session', (t) async {
    final h = await _pumpOrbRoute(t);
    await t.tap(find.byKey(const ValueKey('voice-close')));
    await t.pumpAndSettle();
    expect(h.server.sentJson.last, {'type': 'end'});
    expect(h.mic.stopped, isTrue);
    expect(h.out.stops, greaterThanOrEqualTo(1));
    expect(find.byType(VoiceModeView), findsNothing);
  });

  testWidgets(
    'orb shows state text, last heard line, streaming reply, and scales with mic level',
    (t) async {
      final h = await _pumpOrbRoute(t);
      expect(find.text('Listening'), findsOneWidget);

      h.server.event({'type': 'state', 'state': 'thinking'});
      h.server.event({'type': 'heard', 'text': 'What is the weather?'});
      await t.pump();
      expect(find.text('Thinking'), findsOneWidget);
      expect(find.text('What is the weather?'), findsOneWidget);

      h.server.event({'type': 'state', 'state': 'speaking'});
      h.server.event({'type': 'reply', 'text': 'Sunny '});
      h.server.event({'type': 'reply', 'text': 'and clear.'});
      await t.pump();
      expect(find.text('Speaking'), findsOneWidget);
      expect(find.text('Sunny and clear.'), findsOneWidget);

      // Emit a loud PCM16 frame and confirm the orb scales up smoothly.
      final loud = ByteData(640);
      for (var i = 0; i < 640; i += 2) {
        loud.setInt16(i, 24000, Endian.little);
      }
      h.mic.emit(loud.buffer.asUint8List());
      await t.pump();
      final scaleWidget = t.widget<AnimatedScale>(
        find.descendant(
          of: find.byKey(const ValueKey('voice-orb')),
          matching: find.byType(AnimatedScale),
        ),
      );
      expect(scaleWidget.scale, greaterThan(1.0));
    },
  );

  testWidgets('error event shows the error message and Close button', (
    t,
  ) async {
    final h = await _pumpOrbRoute(t);
    h.server.event({'type': 'error', 'message': 'Voice is not configured.'});
    await t.pump();

    expect(find.text('Voice is not configured.'), findsOneWidget);
    expect(find.byKey(const ValueKey('voice-close')), findsOneWidget);
  });
}
