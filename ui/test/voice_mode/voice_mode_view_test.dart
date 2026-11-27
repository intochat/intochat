import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
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

Widget _host(Widget child, {Brightness brightness = Brightness.light}) =>
    MaterialApp(
      theme: UiTheme.intochat(brightness),
      home: Scaffold(body: Center(child: child)),
    );

Future<
  ({
    _FakeServer server,
    _FakeMic mic,
    _FakeOut out,
    ValueNotifier<bool> open,
    int Function() connectCount,
  })
>
_pumpDockedHud(
  WidgetTester tester, {
  Key? hudKey,
  bool initialExpanded = false,
  Brightness brightness = Brightness.light,
}) async {
  final server = _FakeServer();
  final socket = server.createSocket();
  final mic = _FakeMic();
  final out = _FakeOut();
  final open = ValueNotifier<bool>(true);
  var connects = 0;

  await tester.pumpWidget(
    _host(
      ValueListenableBuilder<bool>(
        valueListenable: open,
        builder: (context, isOpen, _) {
          if (!isOpen) return const Text('Closed');
          return VoiceModeView(
            key: hudKey,
            connect: () async {
              connects++;
              return socket;
            },
            mic: mic,
            out: out,
            initialExpanded: initialExpanded,
            onClose: () => open.value = false,
          );
        },
      ),
      brightness: brightness,
    ),
  );
  await tester.pump();
  return (
    server: server,
    mic: mic,
    out: out,
    open: open,
    connectCount: () => connects,
  );
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
        MaterialApp(
          theme: UiTheme.intochat(Brightness.light),
          home: VoiceModeView(connect: () async => socket, mic: mic, out: out),
        ),
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
    final h = await _pumpDockedHud(t);
    h.server.event({'type': 'cancel'});
    await t.pump();
    expect(h.out.flushes, 1);
  });

  testWidgets('tapping the orb interrupts', (t) async {
    final h = await _pumpDockedHud(t);
    await t.tap(find.byKey(const ValueKey('voice-orb')));
    await t.pump();
    expect(h.server.sentJson.last, {'type': 'interrupt'});
    expect(h.out.flushes, 1);
  });

  testWidgets('close button and Escape key end the session cleanly', (t) async {
    final h = await _pumpDockedHud(t);
    await t.tap(find.byKey(const ValueKey('voice-close')));
    await t.pump();
    expect(h.server.sentJson.last, {'type': 'end'});
    expect(h.mic.stopped, isTrue);
    expect(h.out.stops, greaterThanOrEqualTo(1));
    expect(find.byType(VoiceModeView), findsNothing);

    final h2 = await _pumpDockedHud(t);
    await t.sendKeyEvent(LogicalKeyboardKey.escape);
    await t.pump();
    expect(h2.server.sentJson.last, {'type': 'end'});
    expect(h2.mic.stopped, isTrue);
    expect(find.byType(VoiceModeView), findsNothing);
  });

  testWidgets(
    'expand/compact toggle switches layout smoothly without reconnecting WebSocket',
    (t) async {
      final h = await _pumpDockedHud(t);
      expect(h.connectCount(), 1);
      expect(find.byKey(const ValueKey('voice-hud-compact')), findsOneWidget);
      expect(find.byKey(const ValueKey('voice-hud-expanded')), findsNothing);

      h.server.event({'type': 'heard', 'text': 'Show quarterly sales'});
      h.server.event({'type': 'reply', 'text': 'Opening the sales report.'});
      await t.pump();

      await t.tap(find.byKey(const ValueKey('voice-expand-toggle')));
      await t.pump();
      await t.pump(const Duration(milliseconds: 250));

      expect(find.byKey(const ValueKey('voice-hud-expanded')), findsOneWidget);
      expect(find.byKey(const ValueKey('voice-hud-compact')), findsNothing);
      expect(h.connectCount(), 1);
      expect(h.server.sentJson.where((e) => e['type'] == 'end'), isEmpty);
      expect(find.text('Show quarterly sales'), findsOneWidget);
      expect(find.text('Opening the sales report.'), findsOneWidget);

      // Audio/mic still flow on the same socket after expanding.
      h.mic.emit(Uint8List(320));
      await t.pump();
      expect(h.server.sentBinary, hasLength(1));

      // Collapse back to compact without reconnecting.
      await t.tap(find.byKey(const ValueKey('voice-expand-toggle')));
      await t.pump();
      await t.pump(const Duration(milliseconds: 250));
      expect(find.byKey(const ValueKey('voice-hud-compact')), findsOneWidget);
      expect(h.connectCount(), 1);
    },
  );

  testWidgets(
    'live tool chips render and transition running -> done / failed and reset on new turn',
    (t) async {
      final h = await _pumpDockedHud(t);

      h.server.event({
        'type': 'tool',
        'callId': 'tc-1',
        'name': 'web_search',
        'status': 'running',
        'label': 'Searching web',
      });
      h.server.event({
        'type': 'tool',
        'callId': 'tc-2',
        'name': 'data_query',
        'status': 'running',
      });
      await t.pump();

      final chip1 = find.byKey(const ValueKey('voice-tool-chip:tc-1'));
      final chip2 = find.byKey(const ValueKey('voice-tool-chip:tc-2'));
      expect(chip1, findsOneWidget);
      expect(chip2, findsOneWidget);
      expect(find.text('Searching web'), findsOneWidget);
      expect(find.text('data_query'), findsOneWidget);
      expect(
        find.descendant(
          of: chip1,
          matching: find.byType(CircularProgressIndicator),
        ),
        findsOneWidget,
      );

      // Transition tc-1 -> done and tc-2 -> failed in place.
      h.server.event({
        'type': 'tool',
        'callId': 'tc-1',
        'name': 'web_search',
        'status': 'done',
        'label': 'Web search complete',
      });
      h.server.event({
        'type': 'tool',
        'callId': 'tc-2',
        'name': 'data_query',
        'status': 'failed',
      });
      await t.pump();

      expect(find.text('Web search complete'), findsOneWidget);
      expect(
        find.descendant(of: chip1, matching: find.byIcon(LucideIcons.check)),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: chip2,
          matching: find.byIcon(LucideIcons.circleAlert),
        ),
        findsOneWidget,
      );

      // New user utterance resets previous turn's tool chips.
      h.server.event({'type': 'heard', 'text': 'Next question'});
      await t.pump();
      expect(chip1, findsNothing);
      expect(chip2, findsNothing);
    },
  );

  testWidgets('PTT toggle and Speaker safe toggle gate mic frames', (t) async {
    final h = await _pumpDockedHud(t);

    // Enable PTT -> frames gated until orb is long-pressed.
    await t.tap(find.byKey(const ValueKey('voice-ptt-toggle')));
    await t.pump();
    expect(find.text('Push-to-talk'), findsOneWidget);
    expect(find.text('Hold orb to speak'), findsOneWidget);

    h.mic.emit(Uint8List(320));
    await t.pump();
    expect(h.server.sentBinary, isEmpty);

    // Disable PTT and enable Speaker safe -> gated while speaking.
    await t.tap(find.byKey(const ValueKey('voice-ptt-toggle')));
    await t.tap(find.byKey(const ValueKey('voice-echo-gate-toggle')));
    h.server.event({'type': 'state', 'state': 'speaking'});
    await t.pump();

    h.mic.emit(Uint8List(320));
    await t.pump();
    expect(h.server.sentBinary, isEmpty);

    h.server.event({'type': 'state', 'state': 'listening'});
    await t.pump();
    h.mic.emit(Uint8List(320));
    await t.pump();
    expect(h.server.sentBinary, hasLength(1));
  });

  testWidgets(
    'orb shows state text, last heard line, streaming reply, and scales with mic level',
    (t) async {
      final h = await _pumpDockedHud(t);
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
    final h = await _pumpDockedHud(t);
    h.server.event({'type': 'error', 'message': 'Voice is not configured.'});
    await t.pump();

    expect(find.text('Voice is not configured.'), findsOneWidget);
    expect(find.byKey(const ValueKey('voice-close')), findsOneWidget);
  });

  testWidgets(
    'responsive without overflow across desktop, narrow, and mobile in light and dark modes',
    (t) async {
      final viewports = <Size>[
        const Size(1280, 800),
        const Size(380, 700),
        const Size(320, 640),
      ];
      for (final brightness in Brightness.values) {
        for (final size in viewports) {
          t.view.physicalSize = size;
          t.view.devicePixelRatio = 1.0;
          addTearDown(t.view.resetPhysicalSize);
          addTearDown(t.view.resetDevicePixelRatio);

          await t.pumpWidget(const SizedBox.shrink());
          await t.pump();

          final h = await _pumpDockedHud(
            t,
            hudKey: ValueKey('hud-$brightness-${size.width}x${size.height}'),
            brightness: brightness,
          );
          h.server.event({
            'type': 'heard',
            'text': 'Compare Q3 and Q4 revenue across all regions',
          });
          h.server.event({
            'type': 'tool',
            'callId': 't1',
            'name': 'data_query',
            'status': 'running',
            'label': 'Querying revenue table',
          });
          h.server.event({
            'type': 'tool',
            'callId': 't2',
            'name': 'focus_show',
            'status': 'done',
            'label': 'Showing chart',
          });
          await t.pump();
          expect(t.takeException(), isNull);

          await t.tap(find.byKey(const ValueKey('voice-expand-toggle')));
          await t.pump();
          await t.pump(const Duration(milliseconds: 250));
          expect(t.takeException(), isNull);
        }
      }
    },
  );
}
