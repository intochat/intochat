import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../core/neuron_method_caller.dart';
import '../nodes/node_context.dart';
import '../theme/wire_node_values.dart';
import '../widgets/voice/audio_out.dart';
import '../widgets/voice/mic_stream.dart';
import '../widgets/voice/talk_socket.dart';

/// Dedicated RPC contract constants and wire helpers for `IConversation`
/// neurons so widget state never hardcodes C# contract names.
abstract final class ConversationRpc {
  static const String contract =
      'IntoChat.OS.Modules.Flutter.Conversations.IConversation';
  static const String textDeltaSignal = 'TextDelta';
  static const String readMethod = 'Read';
  static const String sayMethod = 'Say';

  /// Normalizes a raw conversation id/key to the `conversations/<id>` form
  /// expected by `IConversation` RPC calls, or returns `null` when empty.
  static String? normalizeConversationKey(String? raw) {
    final trimmed = raw?.trim();
    if (trimmed == null || trimmed.isEmpty) return null;
    return trimmed.startsWith('conversations/')
        ? trimmed
        : 'conversations/$trimmed';
  }

  /// Serializes an [AttachmentNode] to the wire JSON expected by
  /// `IConversation.Say`.
  static Map<String, Object?> attachmentToWireJson(AttachmentNode attachment) =>
      {
        'id': attachment.id,
        'kind': attachment.kind,
        'fileName': attachment.fileName,
        'mediaType': attachment.mediaType,
        'sizeBytes': attachment.sizeBytes,
        'url': attachment.url,
        if (attachment.durationMs != null) 'durationMs': attachment.durationMs,
        if (attachment.transcript != null) 'transcript': attachment.transcript,
      };
}

/// Active full-duplex voice session owned by [ConversationController].
final class ConversationVoiceSession {
  const ConversationVoiceSession({
    required this.conversation,
    required this.connect,
    required this.mic,
    required this.out,
  });

  final String conversation;
  final Future<TalkSocket> Function() connect;
  final MicStream mic;
  final AudioOut out;
}

/// Coordinates non-visual conversation state for `ConversationView`:
/// - ephemeral `TextDelta` stream accumulation & gap healing
/// - `SoLoudAudioOut` read-aloud and full-duplex voice playback lifecycle
/// - `IConversation` attachment submission via [ConversationRpc]
final class ConversationController extends ChangeNotifier {
  ConversationController({
    this._messages = const <MessageNode>[],
    this._deltas,
    this._readConversation,
    this._createAudioOut,
    this._createMicStream,
    this._speak,
    this._talk,
    this._talkConversation,
  }) {
    _subscribeDeltas();
  }

  List<MessageNode> _messages;
  Stream<Map<String, dynamic>>? _deltas;
  Future<Map<String, dynamic>?> Function()? _readConversation;
  AudioOut Function()? _createAudioOut;
  MicStream Function()? _createMicStream;
  VoiceSpeaker? _speak;
  VoiceTalker? _talk;
  String? _talkConversation;

  StreamSubscription<Map<String, dynamic>>? _deltaSub;
  ({String entry, String text})? _pending;
  final List<WireTextDelta> _queuedDeltas = <WireTextDelta>[];
  bool _healing = false;
  int _healEpoch = 0;

  AudioOut? _readAloudOut;
  String? _readingMessageId;
  int _readAloudEpoch = 0;

  ConversationVoiceSession? _voiceSession;
  bool _disposed = false;

  /// Current pending delta buffer (`entry`, `text`), if any.
  ({String entry, String text})? get pending => _pending;

  /// Whether a gap-healing `IConversation.Read` request is in flight.
  bool get isHealing => _healing;

  /// Message id currently being read aloud, or `null` when idle.
  String? get readingMessageId => _readingMessageId;

  /// Active full-duplex voice session, or `null` when voice mode is closed.
  ConversationVoiceSession? get voiceSession => _voiceSession;

  /// Returns the pending streamed text override for [message] when it is
  /// still running and ahead of the persisted snapshot text.
  String? pendingTextFor(MessageNode message) {
    final pending = _pending;
    if (pending != null &&
        pending.entry == message.id &&
        parseMessageStatus(message.status) == WireMessageStatus.running &&
        pending.text.length > message.text.length) {
      return pending.text;
    }
    return null;
  }

  /// Synchronizes widget inputs on `didUpdateWidget`.
  void update({
    required List<MessageNode> messages,
    Stream<Map<String, dynamic>>? deltas,
    Future<Map<String, dynamic>?> Function()? readConversation,
    AudioOut Function()? createAudioOut,
    MicStream Function()? createMicStream,
    VoiceSpeaker? speak,
    VoiceTalker? talk,
    String? talkConversation,
  }) {
    final deltasChanged = !identical(deltas, _deltas) && deltas != _deltas;
    _messages = messages;
    _deltas = deltas;
    _readConversation = readConversation;
    _createAudioOut = createAudioOut;
    _createMicStream = createMicStream;
    _speak = speak;
    _talk = talk;
    _talkConversation = talkConversation;

    if (deltasChanged) {
      clearPending();
      _subscribeDeltas();
    } else {
      _syncPendingWithMessages();
    }

    final nextTalkConversation = _talkConversation?.trim();
    if (_voiceSession != null &&
        (_talk == null ||
            nextTalkConversation == null ||
            nextTalkConversation.isEmpty ||
            _voiceSession!.conversation != nextTalkConversation)) {
      _voiceSession = null;
    }
  }

  void _notify() {
    if (!_disposed) {
      notifyListeners();
    }
  }

  void _subscribeDeltas() {
    _deltaSub?.cancel();
    _deltaSub = _deltas?.listen(_onDelta, onError: (Object _) {});
  }

  /// Clears any buffered delta state and invalidates in-flight gap heals.
  void clearPending() {
    _pending = null;
    _queuedDeltas.clear();
    _healing = false;
    _healEpoch++;
  }

  MessageNode? _findMessage(String id) {
    for (final msg in _messages) {
      if (msg.id == id) return msg;
    }
    return null;
  }

  void _syncPendingWithMessages() {
    final pending = _pending;
    if (pending == null) return;
    final msg = _findMessage(pending.entry);
    if (msg == null) return;
    if (parseMessageStatus(msg.status) != WireMessageStatus.running) {
      clearPending();
      return;
    }
    if (msg.text.length > pending.text.length) {
      _pending = (entry: msg.id, text: msg.text);
    }
  }

  void _onDelta(Map<String, dynamic> payload) {
    final parsed = parseTextDelta(payload);
    if (parsed == null || parsed.delta.isEmpty) return;

    final existing = _findMessage(parsed.entry);
    if (existing != null &&
        parseMessageStatus(existing.status) != WireMessageStatus.running) {
      return;
    }

    if (_pending == null || _pending!.entry != parsed.entry) {
      _queuedDeltas.clear();
      _healing = false;
      _healEpoch++;
      _pending = (entry: parsed.entry, text: existing?.text ?? '');
    }

    if (_healing) {
      _queuedDeltas.add(parsed);
      return;
    }

    final current = _pending!.text;
    if (parsed.at > current.length) {
      _queuedDeltas.add(parsed);
      unawaited(_healGap(parsed.entry));
      return;
    }

    if (_applyDelta(parsed)) {
      _notify();
    }
  }

  bool _applyDelta(WireTextDelta delta) {
    final pending = _pending;
    if (pending == null || pending.entry != delta.entry) return false;
    final current = pending.text;
    if (delta.at == current.length) {
      _pending = (entry: pending.entry, text: '$current${delta.delta}');
      return true;
    }
    if (delta.at < current.length) {
      final overlap = current.length - delta.at;
      if (overlap < delta.delta.length) {
        final suffix = delta.delta.substring(overlap);
        _pending = (entry: pending.entry, text: '$current$suffix');
        return true;
      }
    }
    return false;
  }

  Future<void> _healGap(String entry) async {
    final reader = _readConversation;
    if (reader == null) {
      _queuedDeltas.clear();
      return;
    }
    _healing = true;
    final epoch = _healEpoch;
    try {
      final view = await reader();
      if (_disposed || epoch != _healEpoch) return;
      final reply = parseActiveConversationReply(view, entry);
      var changed = false;
      if (reply != null &&
          _pending != null &&
          _pending!.entry == entry &&
          reply.length > _pending!.text.length) {
        _pending = (entry: entry, text: reply);
        changed = true;
      }
      _queuedDeltas.sort((a, b) => a.at.compareTo(b.at));
      final queued = List<WireTextDelta>.of(_queuedDeltas);
      _queuedDeltas.clear();
      for (final delta in queued) {
        if (_applyDelta(delta)) changed = true;
      }
      if (changed) _notify();
    } catch (_) {
      if (epoch == _healEpoch) {
        _queuedDeltas.clear();
      }
    } finally {
      if (epoch == _healEpoch) {
        _healing = false;
      }
    }
  }

  /// Stops any active read-aloud clip and resets [readingMessageId].
  Future<void> stopReadAloud() async {
    _readAloudEpoch++;
    if (_readingMessageId != null) {
      _readingMessageId = null;
      _notify();
    }
    final out = _readAloudOut;
    if (out != null) {
      await out.stop().catchError((Object _) {});
    }
  }

  /// Starts or stops read-aloud playback for [messageId], cleanly stopping any
  /// previously playing clip before synthesizing the new utterance.
  Future<void> toggleReadAloud(String messageId, String text) async {
    final speak = _speak;
    if (speak == null) return;
    if (_readingMessageId == messageId) {
      await stopReadAloud();
      return;
    }
    final trimmed = text.trim();
    if (trimmed.isEmpty) return;
    final epoch = ++_readAloudEpoch;
    await _readAloudOut?.stop().catchError((Object _) {});
    if (_disposed || epoch != _readAloudEpoch) return;
    _readingMessageId = messageId;
    _notify();
    final out = _readAloudOut ??= (_createAudioOut ?? SoLoudAudioOut.new)();
    try {
      final wav = await speak(trimmed);
      if (_disposed || epoch != _readAloudEpoch) return;
      await out.playClip(wav);
    } catch (_) {
      // Recover button state cleanly when synthesis or playback fails.
    } finally {
      if (!_disposed && epoch == _readAloudEpoch) {
        _readingMessageId = null;
        _notify();
      }
    }
  }

  /// Opens or closes full-duplex voice mode for [conversation]. Opening voice
  /// mode stops any active read-aloud playback and constructs a fresh
  /// [AudioOut] (defaulting to [SoLoudAudioOut] with smooth barge-in).
  void toggleVoiceMode(String conversation) {
    if (_voiceSession != null) {
      closeVoiceMode();
      return;
    }
    final talk = _talk;
    if (talk == null) return;
    unawaited(stopReadAloud());
    final mic = (_createMicStream ?? RecordMicStream.new)();
    final out = (_createAudioOut ?? SoLoudAudioOut.new)();
    _voiceSession = ConversationVoiceSession(
      conversation: conversation,
      connect: () => talk(conversation),
      mic: mic,
      out: out,
    );
    _notify();
  }

  /// Closes the active voice session, if any.
  void closeVoiceMode() {
    if (_voiceSession == null) return;
    _voiceSession = null;
    _notify();
  }

  /// Submits [draft] and [attachments] via [explicitSend] when provided, or
  /// via [callNeuron] using [ConversationRpc.contract] / [ConversationRpc.sayMethod].
  /// Returns `false` when neither transport is configured.
  Future<bool> sendWithAttachments({
    required String draft,
    required List<AttachmentNode> attachments,
    required MultimodalSubmitter? explicitSend,
    required NeuronMethodCaller? callNeuron,
    required String? conversationKey,
  }) async {
    if (explicitSend != null) {
      await explicitSend(draft, attachments);
      return true;
    }
    final convKey = ConversationRpc.normalizeConversationKey(conversationKey);
    if (callNeuron != null && convKey != null) {
      await callNeuron(
        ConversationRpc.contract,
        convKey,
        ConversationRpc.sayMethod,
        [
          draft,
          null,
          [
            for (final item in attachments)
              ConversationRpc.attachmentToWireJson(item),
          ],
        ],
      );
      return true;
    }
    return false;
  }

  @override
  void dispose() {
    _disposed = true;
    _readAloudEpoch++;
    final readAloudOut = _readAloudOut;
    _readAloudOut = null;
    if (readAloudOut != null) {
      unawaited(readAloudOut.stop().catchError((Object _) {}));
    }
    _voiceSession = null;
    _deltaSub?.cancel();
    _deltaSub = null;
    super.dispose();
  }
}
