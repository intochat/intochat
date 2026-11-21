import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:web_socket_channel/web_socket_channel.dart';

import 'talk_ws_io.dart'
    if (dart.library.js_interop) 'talk_ws_web.dart'
    as talk_ws;

/// Voice-mode phase reported by the Kernel over `/ai/talk`.
enum TalkPhase { listening, thinking, speaking }

/// Control/status event sent by the Kernel as a JSON text frame on `/ai/talk`.
sealed class TalkEvent {
  const TalkEvent();

  factory TalkEvent.fromJson(Map<String, Object?> json) {
    final type = json['type'];
    return switch (type) {
      'state' => TalkStateEvent(_parsePhase(json['state'])),
      'heard' => TalkHeardEvent('${json['text'] ?? ''}'),
      'reply' => TalkReplyEvent('${json['text'] ?? ''}'),
      'cancel' => const TalkCancelEvent(),
      'error' => TalkErrorEvent('${json['message'] ?? ''}'),
      _ => throw FormatException('Unknown talk event type: $type'),
    };
  }

  static TalkPhase _parsePhase(Object? raw) =>
      switch ('$raw'.trim().toLowerCase()) {
        'thinking' => TalkPhase.thinking,
        'speaking' => TalkPhase.speaking,
        _ => TalkPhase.listening,
      };
}

final class TalkStateEvent extends TalkEvent {
  const TalkStateEvent(this.state);

  final TalkPhase state;
}

final class TalkHeardEvent extends TalkEvent {
  const TalkHeardEvent(this.text);

  final String text;
}

final class TalkReplyEvent extends TalkEvent {
  const TalkReplyEvent(this.text);

  final String text;
}

final class TalkCancelEvent extends TalkEvent {
  const TalkCancelEvent();
}

final class TalkErrorEvent extends TalkEvent {
  const TalkErrorEvent(this.message);

  final String message;
}

/// Full-duplex WebSocket wrapper for `GET /ai/talk?conversation={id}`.
///
/// Splits incoming frames by kind: JSON text frames stream through [events] and
/// binary PCM16 frames stream through [audio].
final class TalkSocket {
  TalkSocket(WebSocketChannel channel) : _channel = channel {
    _events = StreamController<TalkEvent>.broadcast(
      sync: true,
      onListen: _ensureStarted,
    );
    _audio = StreamController<Uint8List>.broadcast(
      sync: true,
      onListen: _ensureStarted,
    );
  }

  /// Opens a platform-appropriate WebSocket connection to [uri].
  static Future<TalkSocket> connect(
    Uri uri, {
    Map<String, String> headers = const {},
  }) async {
    final channel = await talk_ws.connectTalkWebSocket(uri, headers: headers);
    return TalkSocket(channel);
  }

  final WebSocketChannel _channel;
  late final StreamController<TalkEvent> _events;
  late final StreamController<Uint8List> _audio;
  StreamSubscription<dynamic>? _sub;
  bool _started = false;
  bool _closed = false;

  Stream<TalkEvent> get events => _events.stream;
  Stream<Uint8List> get audio => _audio.stream;

  void _ensureStarted() {
    if (_started || _closed) return;
    _started = true;
    _sub = _channel.stream.listen(
      _onFrame,
      onError: (Object error, StackTrace _) {
        if (!_events.isClosed) {
          _events.add(TalkErrorEvent('$error'));
        }
      },
      onDone: () {
        if (!_events.isClosed) unawaited(_events.close());
        if (!_audio.isClosed) unawaited(_audio.close());
      },
    );
  }

  void _onFrame(dynamic frame) {
    if (frame is String) {
      try {
        final decoded = jsonDecode(frame);
        if (decoded is Map) {
          final map = Map<String, Object?>.from(decoded);
          if (!_events.isClosed) {
            _events.add(TalkEvent.fromJson(map));
          }
        }
      } catch (_) {
        // Ignore malformed or unknown control frames.
      }
      return;
    }
    if (frame is Uint8List) {
      if (!_audio.isClosed) _audio.add(frame);
      return;
    }
    if (frame is List<int>) {
      if (!_audio.isClosed) _audio.add(Uint8List.fromList(frame));
    }
  }

  /// Sends a raw PCM16 LE 16 kHz mono mic frame to the Kernel.
  void send(Uint8List pcm16) {
    if (_closed || pcm16.isEmpty) return;
    try {
      _channel.sink.add(pcm16);
    } catch (_) {}
  }

  /// Requests immediate barge-in / interruption of the current spoken turn.
  void interrupt() {
    if (_closed) return;
    try {
      _channel.sink.add(jsonEncode(const {'type': 'interrupt'}));
    } catch (_) {}
  }

  /// Sends `{"type":"end"}` and closes the underlying WebSocket cleanly.
  Future<void> close() async {
    if (_closed) return;
    _closed = true;
    try {
      _channel.sink.add(jsonEncode(const {'type': 'end'}));
    } catch (_) {}
    try {
      unawaited(_channel.sink.close().catchError((Object _) {}));
    } catch (_) {}
    await _sub?.cancel();
    _sub = null;
    if (!_events.isClosed) await _events.close();
    if (!_audio.isClosed) await _audio.close();
  }
}
